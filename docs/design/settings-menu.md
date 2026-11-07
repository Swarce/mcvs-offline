# The game's Settings menu: the trim and the Android PERFORMANCE page

Status: built in build 19 (`hosts/embedded/Admin/SettingsMenu/`, `hosts/embedded/Graphics/PerformancePrefs.cs`); build
20 (`4079f39`) replaces the grid's single step per focus-in with a run of frames (§3.2) after the owner saw build 19's
stock menu again after a tab and Back on both platforms; the device cause is not known, and build 20's log names it. Build
21 (`d74a07a`, APK and PC kit) lays the grid out in the focus-in frame itself (§3.2, "Build 21"), after the owner saw build
20's stock grid flash "for a split second" on every return to Settings (the Back bug itself was fixed by build 20); the run
per focus-in stays as the safety net. Build 22 (`e1bb9b9`, APK and PC kit; merged from `wip/b22-switch`) makes Android's `setup()` stock writes draw nothing,
after the owner saw build 21 still flash MORE GAMES and the FORUM tile on a return (§3.2, "Build 22"), and adds the switch
`admin.settingsMenu.sameFrame` with its automatic fallback (§5 risk 2). Build 23 (`wip/b23-phone`) answers the owner's build-22
report from the S22 (§3.2, "Build 23"): the PERFORMANCE title back at the tiles' height, ABOUT drawn centred on MORE GAMES'
button with the switch `admin.settingsMenu.aboutCentre`, no same-frame look when the dress before `setup()` proved it
needless, and timings in the log lines. Build 23 has not yet been seen on a phone or on Windows. This is the final design: the review corrections (A1-A3, B1-B2, C1, D1-D2, E1-E7, F1
of the design review) are folded in, and each place says which one it is.

Tags: [V] checked in the exported ActionScript, the decompiled C#, the game data or the repo code. [I] inferred from the
code, not run. [G] guess (a device check decides).

References to the game's own code (not in the repo; they are game data):

* `AND-AS/`, `PC-AS/`: the exported ActionScript of the Android (1.17.32) and Steam (1.17.30) `UI.swf`.
  `MS` = `menus/settings/MenuSettings.as`, `MGS` = `menus/settings/MenuGameplaySettings.as`.
* `AND-CS/`, `PC-CS/`: the decompiled C# of the Android and Steam assemblies.

## 0. Requirements and decisions

The owner's requirements (October 2026) and their answers to the five open questions (all five: the recommended default):

| # | Requirement | Mechanism | IL patch | Game data edit |
|---|---|---|---|---|
| R1 | PC Settings: remove SOCIAL MEDIA, FORUM, CUSTOMER CARE; the six tiles left as two centred rows of three; ABOUT centred alone | The host appends its own delegate to the Flash native `EnterSection`. On `"settings_main_screen"` it posts a layout step that moves the live tiles (the build-13 technique of the Account menu) | none | none |
| R2 | Android Settings: remove FORUM, SOCIAL MEDIA, CUSTOMER CARE, MORE GAMES; add PERFORMANCE; 4 + 3 tiles, ABOUT centred | The same step with Android numbers. `btn_forum` is re-dressed as PERFORMANCE and its native `GoToForum` is taken over | none | none |
| R3 | Android PERFORMANCE page with Steam's options, less FULLSCREEN, SCREEN RESOLUTION and FIELD OF VIEW (owner, messages 3 and 4) and VSYNC (answer 2) | `menu_gameplaySettings` in a host "Performance mode": the host rebuilds its rows into the Performance rows. The values live in our `settings.json` (`graphics.*`) and the `graphics` hooks apply them | none new (patch `graphics` exists) | none |
| R4 | Android Gameplay Settings without GRAPHICS SETTINGS and FRAME RATE LIMIT, in the main menu and in a match (answer 4) | Our delegates on `CanChangeGraphicSetting` / `CanChangeMaxFramerate` return `false` outside Performance mode, so the game never builds the two rows | none | none |

Owner answers: (1) GRAPHICS is AUTO / MID / HIGH / ULTRA, bound to `graphics.profile`, with a restart prompt; (2) no
VSYNC on Android; (3) FRAME RATE LIMIT offers 30 / 60 / 90 / 120 / UNLIMITED; (4) FRAME RATE LIMIT leaves Gameplay
everywhere on Android, in-match too (the main-menu PERFORMANCE page is then the only place to change it); (5) English
labels for ANTIALIASING, SCREEN SCALE and LOW RESOLUTION MAPS, the page title PERFORMANCE stays translated.

**Why not edit `UI.swf`.** On PC it breaks invariant P1, "no Steam file changes" ([pc-port.md](pc-port.md) §1.3). On
Android it needs a player-side SWF producer and re-wrapping the data container and the installer index, and nobody
knows whether gameswf accepts re-emitted ABC [G]. Every piece the runtime route touches is public C# or a dynamic AS
member.

**Why runtime delegates need no IL.** `MenuComponent.OverrideNativeFunction(name, Delegate, removePrevious,
replaceOnlyMatching)` is public on both platforms (AND-CS `MenuComponent.cs:733`, PC `:715`), and the dispatch calls
every registered delegate, **the last non-null return wins** (AND `:895-899`, PC `:880`) [V].

**Everything can be turned off** (settings page, or `settings.json`):

| Key | Default | Platforms | Off means |
|---|---|---|---|
| `admin.settingsMenu.trim` | `true` | both | the stock Settings grid at the next opening |
| `admin.settingsMenu.performance` | `true` | Android (hidden on PC) | no PERFORMANCE tile (FORUM stays hidden while the trim is on), Gameplay Settings keeps GRAPHICS SETTINGS and FRAME RATE LIMIT, the 90/120 list is not offered from the next start and the stored index is mapped back |
| `graphics.msaa` | `auto` | Android | `auto`: the profile's own MSAA, the GameOptions text byte-identical |
| `graphics.renderScale` | `auto` | Android | `auto`: the profile's own scale |
| `graphics.lowResMaps` | `auto` | Android | `auto`: the profile's own layers |
| `graphics.fpsListVersion` | absent | Android, read-only | written by the host only (§3.4) |

## 1. What each screen looks like

Positions are panel units, origin at the viewport centre **+50 in y** in the main menu (AND-AS/MS:464, PC-AS/MS:631;
review A3: no effect on horizontal centring). Tiles are `BtnSetting`, 465.35 × 355.05, registration point top-left.
ABOUT is a `ThinBtn`, centred on its 365.8-wide plate, not on its runtime `.width` (about 401).

### 1.1 PC (Steam 1.17.30), main-menu Settings (panel frame 1)

```
        [ GAMEPLAY SETTINGS ] [ AUDIO     ] [ ACCOUNTS    ]
        [ HELP              ] [ LANGUAGES ] [ PERFORMANCE ]
                         ( ABOUT )
```

| row | tile (instance) | x | y |
|---|---|---|---|
| 1 | GAMEPLAY SETTINGS (`btn_general`) · AUDIO (`btn_audio`) · ACCOUNTS (`btn_account`) | -715.6 · -232.7 · 250.2 | -427.9 |
| 2 | HELP (`btn_help`) · LANGUAGES (`btn_language`) · PERFORMANCE (`btn_performance`) | -715.6 · -232.7 · 250.2 | -50.9 |
| bottom | ABOUT (`btn_credits`) | -182.9 | 354.1 |
| hidden | `btn_social`, `btn_forum`, `btn_customer` (and `btn_more`, which the game already hides, PC-AS/MS:253-258) | | |

* The order is the stock reading order without the removed tiles. The columns are Gameloft's own centred 3-wide grid
  (the Kayac frame 4 of the same SWF) [V]. At 1920×1080 the grid spans screen x 289-1631 and ABOUT 789-1131, both
  centred on 960 [I].
* ABOUT (review A2): Gameloft's own one-button constant is -177 (the middle button's x, AND-AS/MS:208, PC-AS/MS:226),
  which leaves the plate 5.9 units right of centre. The owner asked for ABOUT centred, so the plate's centre (-182.9)
  it is; test T-B1 pins it.
* The stock code moves CUSTOMER CARE and ABOUT relative to their current x on every focus-in on Steam, so they drift
  (PC-AS/MS:253-258) [V]; our absolute writes end that drift.
* **Unchanged on PC:** every submenu, Steam's own PERFORMANCE page with its 9 rows, the in-match Settings panel
  (frame 2), the Kayac and Chinese frames 4 and 5. On PC only `EnterSection` is registered.

### 1.2 Android (1.17.32), main-menu Settings (panel frame 3)

```
  [ GAMEPLAY SETTINGS ] [ AUDIO ] [ CUSTOMIZE CONTROLS ] [ ACCOUNTS ]
          [ HELP ] [ LANGUAGES ] [ PERFORMANCE ]
                          ( ABOUT )
```

| row | tile (instance) | x | y |
|---|---|---|---|
| 1 | GAMEPLAY SETTINGS (`btn_general`) · AUDIO (`btn_audio`) · CUSTOMIZE CONTROLS (`btn_customize`) · ACCOUNTS (`btn_account`) | -948 · -471.05 · 6.85 · 484 | -427.9 |
| 2 | HELP (`btn_help`) · LANGUAGES (`btn_language`) · **PERFORMANCE** (`btn_forum`, re-dressed) | -709.4 · -232.0 · 245.4 | -50.9 |
| bottom | ABOUT: build 23 `btn_more` titled ABOUT (`admin.settingsMenu.aboutCentre` on); `btn_credits` with it off | -177 (build 23, setup()'s own centre); 208 (build 22 and the switch off); -182.9 until build 21 | 354.1 |
| hidden | `btn_social`, `btn_customer`, and `btn_credits` (build 23) or `btn_more` (switch off, build 22) (build 22: also off the panel, y 4000, alpha 0) | | |

* **Order.** The PC order with CUSTOMIZE CONTROLS after AUDIO, so both platforms read the same way and PERFORMANCE
  comes last, as on Steam.
* Row 1 keeps the stock columns, already centred (S22 screen x 152-1849) [V]. Row 2 keeps row 1's pitch (about 477.4)
  and is centred on row 1's centre [I].
* **ABOUT at 208 (build 22, by design):** where `setup()` itself puts it on every focus-in (AND-AS/MS:213, `btn_more.visible`
  having just been set true at :210), so that write draws nothing new. Centring it needs another x than setup's, and every
  way to keep that against :213 failed review: shifting ABOUT's children by -390.9 does not hold, because ThinBtn's own
  timeline moves them on every button state (`_library.swf` sprite 1047: `mc_bg` x 0 on frames 1-14, 8.85 to 17.25 on
  frames 15-44 from `focus_in` to `enabled_end`; `txt` -2.9 to 2.25) [V], so a tap or a focus change would snap the plate
  back or freeze its press animation. BtnBase and BtnSimple themselves write no child x (only `txt.y`, BtnSimple.as:85-88)
  [V]. ABOUT therefore sits right of centre on the bottom row, its stock place. **Build 23:** ABOUT is drawn on MORE GAMES'
  button instead, which `setup()` itself shows at its one-button centre -177 (:210, :212); see §3.2, "Build 23". The plate
  is 5.9 units (about 5 px on the S22) right of builds 19-21's -182.9: exact centring would need a write after `setup()`.
* With `admin.settingsMenu.performance` off and the trim on, FORUM stays hidden and row 2 is HELP and LANGUAGES under
  row 1's two middle columns (-471.05, 6.85).
* **The PERFORMANCE tile** is `btn_forum` re-labelled from C#: `setup({title:"setting.Performance",
  icon:"Menus/assets/png/icons/settings_performance.png"})` (AND-AS `elements/BtnBase.as:116-133`). The key is
  translated in every Android `setting_*` table and the icon ships in the Android pack [V]. Android has no
  `btn_performance` instance, and the AS handler for that name is gated to platforms 4 and 6 (AND-AS/MS:193-203,
  :305-309) [V]. Of the tiles being removed, `btn_forum` is the only one whose tap reaches C# through a native we can
  take over (`GoToForum`, AND-AS/MS:282-284). The ThinBtns CUSTOMER CARE and MORE GAMES also reach C#
  (`GoToCostumerCare`, `GoToFreeGames`, :268-281), but they are hidden (review A1).
* **Unchanged on Android:** the in-match Settings panel (frame 2: AUDIO, GAMEPLAY, CUSTOMIZE). It has no Performance
  tile, and none can be added without an SWF edit (answer 4). The Kayac and Chinese frames.

### 1.3 Android, Gameplay Settings (normal mode)

Today's list (AND-AS/MGS:82-213) minus two rows, with no gap and no orphan header (review D1 corrected the lists):

* **CONTROLS:** SHOOTING MODE (`EnableAutoShoot`), AIM DOWN SIGHTS (`IronSightMode`).
* **GENERAL:** CAMERA SENSITIVITY, CAMERA SMOOTHING, SPRINT SENSITIVITY, FIELD OF VIEW (`PlayerFov`, stays here), INVERT
  X, INVERT Y, AIM ASSIST. GRAPHICS SETTINGS (spliced in at index 2, MGS:245-254) is gone, so the GENERAL header lands
  before CAMERA SENSITIVITY.
* **ADVANCED OPTIONS:** AIM BEHAVIOR (its category is `setting.AdvancedOptions`, MGS:135-139), EXIT IRON SIGHT …
  RETURN TO IRON-SIGHT AFTER RELOADING. FRAME RATE LIMIT is gone with its 'i' button (MGS:229-244, :375-379); it was
  pushed after the gamepad rows, which are skipped before the header logic (:291-299), so no header is orphaned [V].
* Each row shows only when it shows today. The in-match Gameplay page loses the same two rows (answer 4).

### 1.4 Android, the PERFORMANCE page

Title PERFORMANCE (`setting.Performance`, translated). No category headers, as on Steam. Rows in Steam's order:

| # | row | title | widget | values |
|---|---|---|---|---|
| 1 | GRAPHICS | `setting.GraphicSetting` (translated) | selector | AUTO / MID / HIGH / ULTRA (answer 1) |
| 2 | ANTIALIASING | English | selector | OFF / 2x / 4x / 8x |
| 3 | SCREEN SCALE | English | slider | 0-100 % (Steam's scale; the slider shows "%", as Steam's does) |
| 4 | FRAME RATE LIMIT | `setting.FrameRateLimit` (translated; review B2: the stock row is kept with its title and 'i' battery warning; Android has no "FPS LIMIT" string) | selector | 30 / 60 / 90 / 120 / UNLIMITED (answer 3) |
| 5 | LOW RESOLUTION MAPS | English | selector | OFF / ON |

* **What each row does:** GRAPHICS and ANTIALIASING apply at the next start; SCREEN SCALE applies at once (while the
  slider moves) and is stored when the page closes or at the next selector change; FRAME RATE LIMIT at once (the game's
  own row); LOW RESOLUTION MAPS from the next level load.
* **The restart prompt.** Back on the grid after a GRAPHICS or ANTIALIASING change, one confirm popup with the game's
  own texts `setting.GraphicSettingMsg`, `menu.ConfirmButton`, `menu.CancelButton` (AND-CS
  `GamePreferencesComponent.cs:203-227`): Yes restarts through `AdminHost.Exit("performance", relaunch: true)`, No keeps
  the choice for the next start. Review B1: this is our Android adaptation, not a copy of Steam's prompt, which is
  informational only, shows only for GRAPHICS (or Notification) and never for MSAA (Steam applies MSAA live).

## 2. The Android PERFORMANCE options, row by row

| Steam row | Decision | Android back end | Why |
|---|---|---|---|
| GRAPHICS (LOW/HIGH/ULTRA, pref `Performance`) | **Adapt** | A selector bound to our `graphics.profile` (auto/mid/high/ultra), applied by patch `graphics` at the next start, with the restart prompt. A change also resets AppPreferences `GameOptions_SelectedProfile` to 0 (saved through LocalStorage), so AUTO really is the game's choice for this phone | The Android `.so` never reads LocalStorage `Performance` [V]. The stock Android row is a relative −1/0/+1 step on the phone's profile, which a forced `graphics.profile` makes inert. One source of truth |
| ANTIALIASING (pref `MSAA`) | **Adapt**, at restart | `graphics.msaa`, injected as GameOptions `renderTargetMSAA` (0/2/4/8) by the `graphics` JSON rewrite in every block that sets it | The engine supports it (`PostProcessManager.RenderTargetAntialiasingSetting`), but Android has no `MSAAChanged` and `ValidateTargets` never rebuilds the MSAA target [V]; a live change would need a new IL patch. 8x support depends on the GPU [G] |
| SCREEN SCALE (pref `RenderScale`) | **Port** | `graphics.renderScale` (`auto` or 0-100): `RenderTargetSizeScale` (and `RenderTargetSizeScaleOld`) = `v > 0.97 ? 1 : 0.4 + 0.6 v` (Steam's formula, PC-CS `GamePreferencesComponent.cs:496-497`), live and at start after `ApplyGameOptions`; also written into the component's `m_options`, since every level load sets the scale again from there (both scales ours, the dynamic scale off) | The member is the same on both platforms [V]; it is the phone's equivalent of resolution. Applied after the GPU profile's own value, or it is overwritten |
| FPS LIMIT (pref `FPS`) | **Adapt**: Android's own FRAME RATE LIMIT row | The stock `MaxFramerate` row (an index into `FpsValues`, real limiter `GameApplication.FramerateLimit`), with GameOptions `maxFramerateValues` "30 60 -1" rewritten to "30 60 90 120 -1" (VeryLowGPU's "23 60 -1" is left alone) | Steam's `WCPFramerateLimit` is a one-instruction `ret` in the Android `libnativeengine.so` [V objdump]. The stock row already works, applies at once and has the battery warning |
| FULL SCREEN | **Drop** (owner) | | No `GameManager.dll`/`nativegame.dll` on Android; always full screen |
| VSYNC | **Drop** (answer 2) | | Android's `GameApplication_SetVsync` is a single `ret` [V objdump]; Android composes on the display's refresh [G]. A row would do nothing |
| SCREEN RESOLUTION | **Drop** (owner) | | No window and no resolution list; SCREEN SCALE covers it |
| FIELD OF VIEW | **Drop from Performance** (owner) | stays in Gameplay as `PlayerFov` | Android Gameplay already has the slider |
| LOW RESOLUTION MAPS (pref `UseLowEndMaps`) | **Port** | `graphics.lowResMaps` (`auto`/`on`/`off`): Steam's handler (PC-CS `GamePreferencesComponent.cs:479-486`) swaps `World.GlobalExcludeLayers` HighEndOnly↔LowEndOnly and sets `GameGameOptionsComponent.LowResMaps`; ours also covers a list holding neither entry. At start after `ApplyGameOptions`, and live for the next level load | The members are the same on both platforms and both layer sets ship in the Android pack [V]. Process-wide, so the in-process match servers load the same layers, as on Steam |

* **Storage.** All values live in `settings.json` (`Settings.SetAndSave`, written on the thread pool, never on the game
  thread). The settings page shows and edits the same keys; a change made there applies at the next start (the host reads
  its own copy at start, and nothing applies a save of the server's copy to the running engine), so all four are
  `restart` keys on the settings page. The game's LocalStorage is not used: the game drops unknown
  preference keys (AND-CS `UserPreferencesComponent.cs:117-149`) [V]. Saves of one key may run in any order; each one
  writes the key's latest pending value, so the file never ends older than the last change.
* **What a row shows at `auto`:** GRAPHICS AUTO; ANTIALIASING the current samples; LOW RESOLUTION MAPS the current
  flag; SCREEN SCALE **the scale a match renders at** (`PerformancePrefs.AutoRenderScaleSlider`: the profile's dynamic
  or fixed match scale from its own `m_options`, else the engine's current one). Not the engine's current one first: the
  page lives in the main menu, whose scale is the menu's (`renderTargetSizeScaleMM`, 1.0), so the S22's MidGPU would
  show 100 % while its matches render at 0.6, and storing the shown value would force matches to full scale.
* **FPS index migration (review E7).** With the longer list a stored `MaxFramerate` index changes meaning (stored 2,
  UNLIMITED, would read 90). After the client World's preferences have loaded (a 1 s timer posts the step until the index
  is readable), a one-time step maps it on the **resolved** `GamePreferencesComponent.FpsValues` to the same frame rate
  (2 → 4), marker `graphics.fpsListVersion` in the server's copy of the settings; with the feature off it maps back (4 →
  2, 90/120 → 60). A VeryLowGPU phone keeps the 3-entry list and is never migrated. Remaining ambiguity: if
  `settings.json` is lost after a migration, a stored 2 reads as stock UNLIMITED (the code comment says so).

## 3. Implementation

### 3.1 The runtime native registry (`SettingsMenu/UiNatives.cs`)

* Started by `EmbeddedHost` right after `AdminHooks.Host = admin`. A `System.Threading.Timer` posts a re-ensure step to
  the game thread every 2 s (`GameReflection.Post`).
* The step registers on the **client World's** `UIComponent` only, never on a match-server World (review E5: the table
  is per `MenuComponent` instance), with `OverrideNativeFunction(name, ours, true, true)`: it removes only our earlier
  delegate (same `Target`; every name has its own target object) and appends again, so ours stays last. It first reads
  the table (read only): a name whose list still ends with ours is left alone. That repairs the game's own
  re-registrations and removals (UIComponent `:402`, `:416`, `:1449`, `:1578`; GameClientComponent `:3815-3816`;
  GamePreferencesComponent `:146-151`, `:167-173`) within 2 s; the log counts them.

| native | mode | platform | does |
|---|---|---|---|
| `EnterSection` | append, returns null | both | on `"settings_main_screen"`: clear the tile flag and Performance mode, post the grid step (§3.2), show the restart prompt if one is pending |
| `GoToForum` | **always replace** (`true, false`; review E4) | Android | tile active: open the page (§3.3); otherwise our delegate calls `UIComponent.GoToForum()` itself, so the stock body and the optional `forum` patch run as before |
| `CanChangeGraphicSetting`, `CanChangeMaxFramerate` | append, boxed `false` or null | Android | R4 and the page (§3.3, §3.5) |
| `SetUserPreference`, `SaveUserPreferences` | append, null | Android | the page's writes (§3.3); ignored entirely while the page transform runs (E1) |
| `GetPlatformId` (Android), `GetLanguage` (PC) | append, returns null, **only for the rest of a Settings focus-in frame** | build 21 | the same-frame grid step (§3.2): appended by `EnterSection("settings_main_screen")` only when the game's own delegate is on the list, removed with `RemoveNativeFunctionOverride(object)` by the run's first frame; not part of the re-ensure |

* Rules (pc-port P4): every delegate catches everything and never blocks, reads its settings on every call, and does
  nothing while `AdminHooks.Host` is null.
* **Fallback** (not built): if a device log shows our `EnterSection` delegate missing when Settings opens, an IL prologue
  on `UIComponent.NativeEnterSection(string)` (AND-CS `UIComponent.cs:795`, PC `:826`).

### 3.2 R1 and R2: the grid (`SettingsMenu/SettingsMenuLayout.cs`)

* `Plan(platform)` returns the constant tables of §1.1 and §1.2 (pure); `Apply` is the live step, posted from
  `EnterSection` (MenuBase.onFocusIn calls it before `MenuSettings.setup()`; the posted step runs at the start of the next
  frame, after `setup()`).
* It reads everything before it writes anything (the build-13 rule): the menu on top of `MenusStack` is `menu_settings`;
  `mc_panel.currentFrame` (1-based) is 1 on Steam or 3 on Android, never 2, 4 or 5; every named instance exists; every
  tile is 465.35 ± 2 wide. Then: on Android first `btn_forum.setup({...})` (if that throws, nothing else moves and FORUM
  keeps forwarding); `visible=false` and `mouseEnabled=false` on the removed instances; absolute `x`/`y` on the kept
  ones. `TileActive` is set only after every write succeeded. One log line: `settings menu: frame 3, 7 tiles + ABOUT laid
  out` or `… not laid out (<reason>)`.
* `setup()` runs on every focus-in, including every return from a submenu (AND-AS/MS:468-473, PC-AS/MS:640-645), and our
  step follows it each time.
* **Build 20: one run per focus-in, not one step: a mitigation and a diagnostic, not a proven fix.** Build 19's single step
  left the stock menu after a tab and Back (owner, both platforms). `setup()` brings the stock grid back on every focus-in: on
  Android it re-dresses FORUM, shows MORE GAMES and moves ABOUT and CUSTOMER CARE (AND-AS/MS:110-119, :208-212); on Steam it
  goes to panel frame 3 and back to 1 (PC-AS/MS:44-52), which places new tile instances (frames 1 and 3 hold them at other
  depths, frame 2 has other characters there), and sets the 4-column x's (:253-345). So the screen after a return is only what
  our step makes of it, and a step that did not land had no second chance. **What kept it from landing on the device is not
  known.** In the gluic order MenusStack.popMenu (gluic/components/menus/MenusStack.as:357-366) runs `focusRemoved`, pops,
  then `focusGiven`, which sends FOCUS_IN synchronously (Menu.as:157-188): EnterSection runs with Settings on top, `setup()`
  right after it, and nothing in the AS positions the panel again later. In that order build 19's step lays the grid out too
  (the fake's default rows, Steam's new instances included, pass on build 19). The rows of `SettingsMenuReturnTests` that fail
  on build 19 are hypothetical models with no AS source: a popped submenu still on top for some frames (refuted by popMenu's
  order) and the stock positioning running again some frames after the return. Not covered by either: Android's `setup()`
  never shows `btn_social` again (AND-AS/MS:204-213), so a stock grid with SOCIAL MEDIA back would mean new instances or a
  re-created menu (`Setting_Menu` is a disposable menu, SwfMain.as:606). `SettingsMenuLayout.Schedule` now starts a run (the
  next focus-in's run supersedes it):
  * each frame it finds `menu_settings` on top of `MenusStack`, else by name while it is in the stack
    (`getMenuIndexInStack`, `getMenuByName`), and reads the panel as before; a failed read is tried again on the next frame;
  * once laid out it reads the grid back (slots ±0.5, removed instances invisible; on Android only while the tile is active)
    until it has stayed laid out for 2 frames after a 45-frame settle window, and lays it out again when the stock grid came
    back; at 120 frames it stops;
  * the frames after the first are posted with `GameSynchronizationContext.DelayedPost` (`ISettingsMenuHost.PostNextFrame`):
    a `Post` from a drained step runs in the same drain (`while (HasWorkItems)`), not on the next frame;
  * a run that has not laid the grid out ends at once when Settings leaves the stack after a frame found it there:
    `… not laid out (<reason>; Settings left after N frames)`;
  * one log line per run: `… laid out` (plus `after N frames (<why it waited>)`, `laid out again Kx after the stock grid came
    back` when a read-back found a kept instance off its slot or a removed one visible, `tile re-dressed again Kx after another
    panel`) or `… not laid out (<reason>; gave up after N frames)`. `RedressFailed` is set by every run that never laid the grid
    out, the early Back included (the in-match grid's R4 gate needs no tile, so a panel this build cannot lay out must keep the
    Gameplay rows), and by a write that threw. After a write, `TileActive` stays on through a failed read unless the panel is
    another one (no `mc_panel`, another frame, an instance missing): only then can the tile be a stock FORUM again, and a tap
    reaches the game's FORUM until the run has re-dressed it;
  * the diagnostic: each run's first frame (after `setup()`) logs
    `settings menu: focus-in N (layouts so far: K): on top <menu>, menu_settings at I in the stack, panel frame F, btn_social
    shown|hidden`. `btn_social shown` after a layout means new instances (nothing in either AS shows it again), `hidden` the
    instances written before. **From the next device run, collect per return to Settings:** that line, and the run's
    `… laid out after N frames (<why>)`, `… laid out again Kx …` or `… not laid out (…)` line; no `focus-in` line at all for
    a return means EnterSection never reached us.
* **Build 21: the grid in the focus-in frame (the same-frame step).** The owner's build-20 report: on returning to Settings
  "the normal menu flashes for a split second before being set to our layout again". Design risk 2 predicted it: the run's
  first step is posted from `EnterSection` to `GameApplication.Context`, whose drain for that frame is over, so it runs at
  the start of the next frame, and the render of the focus-in frame draws what `setup()` left (one render: 33 ms at 30 fps,
  17 ms at 60). Nothing covers that frame: Settings has `needTransition` false (no alpha fade, MenuBase.onAnimate), the
  companion's tween groups are empty for menus and `Setting_Menu` has no `focus_in` label; `visible=true` is set by
  Menu.focusGiven before `setup()` (Menu.as:160). What flashes: on Android (same instances, `gotoAndStop(3)` on frame 3 is a
  no-op) FORUM's dress, MORE GAMES and the stock bottom row; on Steam (frame 3 then 1, new instances) the whole stock grid.
  * **Frame order** [V decompiled, both platforms line for line the same here]: `GameApplication.UpdateWorlds` runs (1)
    `PreUpdateManagers`, (2) `GameApplication.Context.ExecuteItems()` (our posts), (3) each World's `Update`: the client
    World's `MenuComponent.OnUpdate` → `Update_private`, the Flash advance in which input is delivered and every AS handler
    runs (the Back: `ButtonSimple` → `FreemiumBar.onButtonRelease` → `MenusStack.popMenu` → `focusGiven` →
    `MenuBase.onFocusIn` → `EnterSection` → `MenuSettings.onFocusIn` → `setup()`, all one synchronous AS call stack; Steam's
    Escape goes `GameInputManagerComponent` → `HudGamepadComponent.BackkeyClick` → `RiseASElementEvent`, the same chain, in
    the same World update), (4) `UpdateManagers` and `UpdateSignal.Set()`, (5) `Render`. Render thread off (Steam, verified
    in the WR2 bootstrap log: `RenderThreadMode None`): `Render_private` draws on the game thread after (3). Render thread on
    (the S22's `Full`, inferred): the render thread's menu pass waits in `UpdateSignal.WaitAndWork` for (4) of the next update.
    Either way a render draws the Flash state some update left, so the only way never to draw the stock grid is to lay it
    out inside the update that ran `setup()`, after `setup()`'s last stock write.
  * **The hook, per platform** (`SettingsMenuLayout.SameFrame`): a native that `setup()` calls after its last stock write.
    Dispatch (`MenuComponent.OnInvokeDelegate`, AND-CS `MenuComponent.cs:869-924`, PC `:855-890`) runs every delegate in order
    and the last non-null result wins, so an appended delegate that returns null observes the call and the game's answer
    stands. `UiNatives` appends it at `EnterSection("settings_main_screen")` (MenuBase.onFocusIn `:339`, before `setup()`)
    and the run's first frame removes it again, so no other menu ever pays for it:
    * **Steam: `GetLanguage`** (registered by the MenuComponent constructor, PC-CS `MenuComponent.cs:334`). PC-AS/MS:367 calls
      it right after the last tile write (`:364 btn_social.x`); after it come only `btn_customer.txt.x` for the language `id`
      (a text field inside a hidden button) and `addButtonListeners` (listeners). The other `GetLanguage` of the path,
      `adjustToViewport`'s (`:633`), runs from MenuBase.onFocusIn `:330`, before `EnterSection`, so before the step is armed.
      Every armed call looks (a read, then the writes when the grid is not laid out): an earlier call would find the grid
      laid out on a return, or be undone by `setup()`'s new instances and looked at again at `:367`.
    * **Android: `GetPlatformId`** (UIComponent.OnStart, AND-CS `UIComponent.cs:1455`). Android's `setup()` has no
      `GetLanguage`; its last stock write is `:213 btn_credits.x`, and the only natives after it are `addButtonListeners`'
      two `GetPlatformId` (`:214` → `:363`) and FreemiumBar's title. `TextUtilities.setStringID` also calls it 3 times per
      label (TextUtilities.as:92, :99), about 40 times per focus-in, so a look runs only when ABOUT sits at the x that `:213`
      writes (208, or 22 without MORE GAMES: never the timeline's 258.05, never ours): false until `:213` on the first entry
      and on a return, true at `:363`, after the FORUM dress (`:116-119`) and every other stock write. The check reads one
      x through a panel handle kept for the frame. One look per focus-in on either platform (at most 4, and none after a look
      whose write threw).
    * A look is the run's own read-then-write (`Attempt` with the read-back): `TileActive` only after every write, no log
      line and no `RedressFailed` from inside the native (the run does both on its frames), nothing thrown back into AS (a
      delegate that throws is rethrown as `MenuInvokationException`), and our own `btn_forum.setup` calls `GetPlatformId`
      again inside the look, which a busy flag ignores. The observer is appended only next to the game's own delegate: alone
      it would answer null (no platform, no language). It also re-enters AS from inside a native, which the game itself does
      (`MoveToCamera` dispatches an AS event synchronously from inside the same `onFocusIn`; `FormatStringWithParameters`
      writes the text field from inside its native) [I].
  * **Why not the others** (two investigations disagreed; the evidence decided): an IL hook in `UpdateWorlds` before
    `UpdateSignal.Set` is equally same-frame on both platforms, but it is a new fingerprinted patch of `Gameloft.Engine` on
    both platforms and leaves §3.1's "natives only" choice; its argument against `GetPlatformId` (an early look sets
    `TileActive`, a later stock FORUM dress undoes it while the read-back trusts it) does not hold with the ABOUT-x check,
    which is false before `:213`, which comes after the FORUM dress, and the look clears `TileActive` first. Posting to a
    later World's context (the in-process match server's) is same-frame only while such a World exists and runs.
    `RenderContext` is drained by the render thread in `Full` mode (Flash writes racing the game thread). The rest of
    `MenuComponent.OnUpdate`, later components and `OnPostUpdate` have no managed callback without IL. `EnterSection` itself
    comes before `setup()`.
  * **The run stays the safety net**, unchanged in its rules: its first frame ends the step; a grid the step laid out counts
    as the run's write at frame 0, so the line reads `… laid out in the focus-in frame`, and the run goes on reading it back
    and lays it out again when needed. Every laid-out line now ends with `stock grid drawn N frames`: the frames whose start
    found a grid that was not ours (not laid out yet, the stock grid back, another panel), each drawn by the render before it
    (0 when the step landed; 1 for build 20's path). The focus-in line ends with what the step did:
    `; same frame: laid out (GetPlatformId calls N, looks 1)`, `; same frame: not laid out, <why> (…)`,
    `; same frame: no look (…)`, `; same frame: not watched (<why>)`, `; same frame: off (trim off)`.
  * **Not covered:** the trim off with the re-dress on (Android): the re-dress alone cannot be told from the stock grid
    before `setup()` ends, so it stays build 20's one frame late. A first read that waits (no `MenusStack`, a width outside
    ±2, a missing instance) leaves it to the run as before. Android's PERFORMANCE icon: the stock `btn_forum.setup` sets the
    FORUM icon and ours sets PERFORMANCE again in the same frame, but `ImageContainer.set img` reloads on every change
    (`removeAllChilds`, then a `Loader` whose COMPLETE shows it, ImageContainer.as:66-108, 207-215), so the tile's icon may
    blink while it loads [G: whether gameswf completes it at once is not known]; that cannot be avoided while the stock
    `setup()` re-dresses FORUM. Not grid: the camera switch (`MoveToCamera`), FreemiumBar's title and back group, and the
    leaving submenu's own hide redraw in the same frame.
* **Build 22: setup()'s stock writes draw nothing (Android), and a switch with a fallback (both platforms).** The owner's
  build-21 report from the S22: "it flashes the more games and the forum button after, the trim remains". That is exactly the
  state at the end of `setup()` before any write of ours, so on the phone the same-frame step did not land or landed too
  early (the likeliest causes, from a read-only diagnosis of five investigations: the look throwing or never called inside
  the native, or build 21's gate opening early when ABOUT could not be read); no device log says which. Build 22 does not
  wait for one: every visible effect of Android's `setup()` goes through a property it writes, so what we need moves onto
  properties it never writes, and its own writes produce our state. No render, early or late, can then show MORE GAMES,
  FORUM or ABOUT elsewhere. On a return `setup()` writes only these on screen [V, AND-AS/MS:42-215]: the FORUM dress of
  `btn_forum` (:114-119, through BtnBase.setup's `setStringID` and `mc_image.img`, BtnBase.as:119, :130-134),
  `btn_more.visible = !IsKayac()` (:210) and the bottom row's x (`btn_customer` -564, `btn_more` -177, `btn_credits` 208,
  :211-213). It writes no y (but `mc_blocker`'s, frame 2), no alpha, no `mouseEnabled` and no child x.
  * **Title: the engine's own LocId remap** (`ForumRemap`, `SettingsMenuLayout.EnsureRemap`). `setting.ForumButton →
    setting.Performance` goes into the client World's `LocalizationFormatterComponent.remapDictionary`, a private
    `Dictionary<LocId, LocId>` (LocalizationFormatterComponent.cs:34) filled once from Remap.json at OnCreate (:91-97,
    :269-292) and applied by `RemapLocId` inside both FormatString overloads before the lookup (:260-267, :422, :494). Every
    menu text goes through it (MenuComponent.FormatString → LocalizationComponent.FormatString → `World.LocalizationFormatter`,
    MenuComponent.cs:1168-1205, LocalizationComponent.cs:335-338, World.cs:438), so `setup()`'s own dress writes the
    translated PERFORMANCE, synchronously, on every focus-in, and `ReloadStringTable` (a language change) re-formats it the
    same way (MenuComponent.cs:1047-1078) [V]. `setting.ForumButton` is used only at AND-AS/MS:117 [V grep]. Copy and swap: a
    new dictionary with the entry replaces the field, never one changed in place. In from the first
    `EnterSection("settings_main_screen")` while `admin.settingsMenu.performance` is on, out when it is off (only our own
    entry: a Remap.json entry for the same id would stay). Pure C#, so not behind the same-frame switch. While it is in,
    `TileActive` is set at once in EnterSection (a tap opens the page, R4 hides the rows, from the focus-in frame on), and
    a run that never laid the grid out sets `RedressFailed` only when `btn_forum` is not on the panel (another frame, the
    tile missing): with the tile there it reads PERFORMANCE whatever our steps did, without it no page is reachable and the
    Gameplay rows must stay. One line per change:
    `settings menu: FORUM remap installed | removed | not installed (<why>)`. Without it (it cannot be installed), build 21's
    re-dress and undress stand.
  * **Icon: out of BtnBase.setup's reach.** Once per `btn_forum` instance it is dressed with PERFORMANCE (the icon loads),
    then the slot `btn_forum.mc_image` is set to null and the container renamed `mcvs_perfIcon`, which covers both ways
    gameswf may resolve `this.mc_image` (the declared var, BtnBase.as:33, or a child by name) [G which one]. `Boolean(this.
    mc_image)` is then false and the stock icon write (:130) is skipped, so the icon neither changes nor reloads
    (ImageContainer.as:207-215). Read back: the slot reads null (`icon slot null`); when it still reaches the container both
    are put back (`icon slot kept (<why>)`) and the look after `setup()`'s last write re-dresses the icon inside `setup()`'s
    stack, as build 21 did; the tile counts as dressed only while its icon is PERFORMANCE's (`img`, a public getter,
    ImageContainer.as:202-205). Undress (a failed write without the remap, or PERFORMANCE switched off) puts the name and the
    slot back first, so `setup()`'s own FORUM icon reaches it again; the dressed container is found by its new name, else
    still as `mc_image` with the slot null (gameswf refused the rename, which `icon slot null` accepts). PERFORMANCE switched
    off is undone by PreDress before `setup()` when the same-frame switch allows it, else by the run's first frame (outside
    any native: the slot back and the stock FORUM dress, `settings menu: FORUM icon put back after setup()`).
  * **MORE GAMES, SOCIAL MEDIA, CUSTOMER CARE: off the panel.** `y` 4000, `alpha` 0, `mouseEnabled` and `mouseChildren`
    false, `visible` false, and `btn_more.x` 0, a marker `setup()` never writes. `setup()`'s `btn_more.visible = true` and
    the x writes then draw nothing, and the hidden MORE GAMES (depth 99, above ABOUT's 69) catches no tap. No scale 0. A kept
    instance that still reads removed (`visible`, `alpha` or `mouseEnabled` off: `btn_forum` hidden while PERFORMANCE was
    off and kept again, the Settings instance unchanged under the admin page) is not in place, and its write puts `visible`,
    alpha 1 and the mouse back before x and y (`setup()` writes none of them on a tile).
  * **ABOUT at 208**, `setup()`'s own x (§1.2): its last stock write is a no-op.
  * **When: `PreDress`, synchronously inside our EnterSection delegate, before `setup()`.** MenuBase.onFocusIn calls
    `EnterSection` (:339) before `MenuSettings.onFocusIn`'s `setup()` (AND-AS/MS:468-473), in the same AS stack [V]. On a
    return the instances are the ones written before (`gotoAndStop(3)` on frame 3 places none), so PreDress only reads them
    back (`kept`); on the first entry the panel is still on frame 1 (a new disposable menu), so PreDress does
    `mc_panel.gotoAndStop(3)` first (setup's own goto is then a no-op) and dresses the new instances (`dressed now`). It
    reads everything before it writes (the run's `Read`; any frame but 3, a missing instance or a width: `not dressed (<why>)`
    and the same-frame step and the run work as in build 21). Order in EnterSection: LeaveMode, the remap, Schedule, PreDress,
    then the observer (after PreDress, whose own `btn_forum.setup` calls `GetPlatformId`), then the restart prompt.
  * **The same-frame step stays, with a new gate:** `btn_more` shown at x -177 within half a twip (`setup()`'s :212 write;
    the timeline's -176.9 and our marker 0 are not), on panel frame 3. It is false whenever any of that cannot be read
    (build 21's gate looked then, and spent its four looks before `setup()`'s dress), and true from :363 on. Its look now
    finds the grid in place and only tidies (MORE GAMES hidden again, its marker back), or re-dresses an icon that was kept
    within reach. Build 20's run stays as read-back and safety net: a removed instance counts as hidden while it is off the
    panel; the frames whose start found MORE GAMES shown again off the panel are counted apart (`; MORE GAMES shown off the
    panel N frames`, drawn as nothing, not in `stock grid drawn`), and a PreDress that left the grid laid out counts as the
    run's write at frame 0 (`… in the focus-in frame`). Because the writes persist, a return draws nothing stock even with the
    switch below off (only the first entry is then one frame late).
  * **`admin.settingsMenu.sameFrame`** (default on, both platforms, live): all our Flash work inside the game's natives,
    which is PreDress (inside EnterSection: setters, `gotoAndStop`, `btn_forum.setup`, AS code run from a native, as the game's
    own FormatString natives write `htmlText`) and the same-frame step (inside GetPlatformId or GetLanguage). Both are
    covered, by one switch, because both re-enter AS from a native, the one thing a device fault could come from; the remap
    is not (no Flash). `Schedule` arms the step only with trim and switch on, and allows PreDress only with the switch on and
    the run posted. Off: build 20's path (the run lays the grid out from the next frame) with the remap.
  * **The breadcrumb** (`SameFrameBreadcrumb`): `settings-menu-same-frame.txt` in the host's log folder (`logs/` of the data
    folder, next to `restart-requested.txt`; McvsOffline\logs on PC), else in the fallback log folder the log files move to
    when that one cannot be written (Android internal storage; %TEMP%\McvsOffline\logs on PC, never a Steam folder that
    cannot be written). Written once per process, right before the first Flash access inside a native (Android: PreDress's
    first read; Steam: the first look), temp file and rename; deleted one frame after the run's first frame
    (`PostNextFrame`): with the render thread on, `GameApplication.Render` starts the frame's `World.RenderAsync` and
    returns (GameApplication.cs:923), and only the next Render waits for it (:900), after the next frame's context drain, so
    the run's first frame can begin while the focus-in frame is still drawn. The marker exists for about two frames, the
    focus-in frame's render included. At the next start (`UiNatives.Start`, off the game thread; or the first
    focus-in) a leftover marker switches `admin.settingsMenu.sameFrame` off in settings.json and logs one WARN line:
    `settings menu: same frame skipped: the last start ended during it (marker <path>); admin.settingsMenu.sameFrame is now
    off; switch it back on to retry`. When the setting cannot be saved the marker stays and the work stays off in this
    process (`… could not be saved as off, so it stays off while the marker is there; delete <path> to retry`: the key still
    reads on, so switching it cannot retry). Marker I/O never throws into AS; a
    marker that cannot be written is said once on the focus-in line (`; breadcrumb not written (<why>)`).
  * **Steam:** unchanged (PC `setup()` goes to frame 3 and back to 1 and writes absolute x for every tile, PC-AS/MS:44-48,
    :253-364, so per-instance tricks would not survive): build 21's GetLanguage step, now behind the switch and the
    breadcrumb.
  * **The lines** per return (Android): `settings menu: focus-in N (layouts so far: L): on top <menu>, …, panel frame 3,
    btn_social hidden; before setup: kept (FORUM remap on, icon slot null, MORE GAMES off the panel, ABOUT 208); same frame:
    laid out (GetPlatformId calls 40, looks 1)` and `settings menu: frame 3, 7 tiles + ABOUT laid out in the focus-in
    frame; stock grid drawn 0 frames`. The first entry says `before setup: dressed now (panel frame 1 to 3, …)`. When the
    phone still cannot run AS from inside GetPlatformId the tail reads `same frame: not laid out, <why>` or `no look`, and
    the run line `… in the focus-in frame; MORE GAMES shown off the panel 1 frame; stock grid drawn 0 frames`: undrawn,
    no flash. `same frame: off (admin.settingsMenu.sameFrame off)` or `off (a breadcrumb of the last start is still there)`
    when switched off.
  * **Tests** (`SettingsMenuBuild22Tests`, on fakes that model setup()'s gotoAndStop, BtnBase's icon branch in three
    resolution models, the image reload, the remap, ReloadStringTable, a refused icon rename, what each render draws and a
    pipelined render thread): 36 cases. The first 24 cover build 22 (21 fail on build 21's code, three are marked regression
    checks); the 12 of the review of build 22 (a tile kept again after PERFORMANCE was off, the R4 net with no tile on the
    panel, the icon put back without the rename or the same-frame switch, the marker across the render thread) fail on
    05a2028's code but one regression check, as do the two changed breadcrumb cases; `SameFrameBreadcrumbTests` for the members alone, and
    the real-assembly check of the remap's members (`RealSettingsMenuMembersTests`).
* **Build 23: the PERFORMANCE title, ABOUT centred, and the gear's stutter (Android; PC only gains log text).** The owner's
  build-22 report from the S22: (P1) the PERFORMANCE tile's title is drawn in the middle of the tile over its icon; (P2)
  ABOUT sits right of centre; (P3) a small stutter on the gear tap. `menu_settings` is a disposable menu (SwfMain.as:606,
  MenusStack createMenu :225-252), so every gear tap builds a new panel on frame 1 and is a first entry for PreDress.
  * **P1, cause [V]:** the only writer of the title's y is BtnSimple.setup: `if (this.needAlign) txt.y = … _loc2_.height / 2`
    (AND-AS elements/BtnSimple.as:83-95; sprite 6716 has neither `mc_hitzone` nor `mc_bg`, so the tile itself, 355.05 / 2 =
    177.5), and `_needAlign` defaults to true (:25). The stock code turns it off just before its own setup (AND-AS/MS:110-120),
    but build 22's PreDress calls `btn_forum.setup` on the new instances first (UiNatives' EnterSection, before setup()), so
    the title is centred and nothing writes it again (the only other `txt.y` writers are BtnSimpleProgress.as:36 and
    BtnShop.as:33). Build 21 had no PreDress. **Fix:** every `btn_forum.setup` of ours on Android goes through
    `ForumSetup`: `needAlign = false` first, read back (BtnSetting is dynamic, swc/settings/BtnSetting.as:6, so a write that
    skips the setter would make a member the getter ignores; only the read-back tells), then the setup, then `TitleRepair`:
    a title below 260 (centred) gets the constant 294.2 (sprite 6716's frame-1 `txt` ty; the timeline animates 290.7 to
    294.2, which is left alone, so btn_help's y is read only for the log) and is read back. PreDress's "kept" branch repairs a
    title a fallback centred earlier, outside the read-back: no title problem reaches `TileReads`, the verify, a re-dress or
    `RedressFailed`. PC (`android` false) is the plain setup; PreDress never runs there. Notes in the focus-in line's
    details: `needAlign off, title y 294.2`, `title y put back (177.52 to 294.2; needAlign refused: <Type: msg>)`,
    `… needAlign not taken (reads true))`, `title y kept (<y>)` only for a y outside 285-300, and once per process `title ref
    294.2, btn_help <y>`; one line per process when the repair fails: `settings menu: PERFORMANCE title y not put back (<why>);
    the title stays centred as in build 22`. No switch: it runs only with PERFORMANCE, and its fallback is build 22.
  * **P2, cause [V]:** `setup()` writes the bottom row by name on every focus-in (AND-AS/MS:208-213: `btn_more.visible =
    !IsKayac()`, `btn_more.x = -177`, `btn_credits.x = 208` while MORE GAMES is shown); build 22 leaves ABOUT at that 208 on
    purpose (above). The only centred x `setup()` writes is MORE GAMES' -177. **Fix (`admin.settingsMenu.aboutCentre`, on by
    default, Android only):** MORE GAMES becomes ABOUT. Both are `swc.lib.Buttons.ThinBtn` at ty 354.10 with a title-only
    setup and the same SFX (:143-166), so the button looks the same. PreDress, with the trim on and only while our
    `GoToFreeGames` is the native's only delegate (EnterSection checks and re-registers it first: OnStart's
    AddBrowserOverrides, UIComponent.cs:395-405, :1467, can put the stock one back up to 2 s before the re-ensure), dresses
    the plan `Plan(false, performance, true)`: `btn_more` kept at -177, 354.1 (mouse back on; `setup()` writes its
    `visible` and x itself) and `btn_credits` off the panel like the removed tiles, its x at the marker 0. After the strict
    read-back it puts in a second remap entry, `setting.FreeGamesButton → setting.CreditButton` (`MoreGamesRemap`, the same
    copy and swap as FORUM's; the id is used only at :161, one string in and_UI.swf; single-hop, LocalizationFormatterComponent.cs:260-265),
    so `setup()`'s own dress titles MORE GAMES' button ABOUT, translated, and ReloadStringTable re-formats it. Any failure
    (`no GoToFreeGames`, `dress refused: <why>`, `read-back <instance>`, `remap refused: <why>`) takes the remap out and
    dresses build 22's bottom row in the same call, and every EnterSection whose PreDress does not draw it (switch off,
    same-frame switch off, breadcrumb, frame 4 or 5, no UI) takes the remap out. Review finding 2: every `EnterSection` of
    another section (MenuBase.as:339 on every menu's focus-in: the main menu's `main_menu_screen`, MenuMainV2.as:136, ABOUT's
    `settings_credits_screen`, MenuAbout.as:48) and the run seeing Settings leave the stack take it out too, with no line
    (`MoreGamesAway`; one line only when the removal fails): Settings' own listeners are gone by then (onFocusOut,
    MenuBase.as:353-357), so a stock `setup()` that runs without our dress (OnStart registers EnterSection again without ours
    and puts the stock GoToFreeGames back, up to 2 s before the re-ensure) titles MORE GAMES as the game does, matching the
    stock tap it gets, and the top bar's Gameloft button in other menus is the game's with no focus read. Settings' next
    focus-in puts it back before `setup()` titles `btn_more` (MenuSettings.as:468-472); `AboutOnMoreGames` stays, as the plan
    of a run still watching the Settings below ABOUT or AUDIO, whose `btn_more` keeps the title it was given. **The real
    ABOUT put back (review findings 3 and 4):** `setup()` never writes `btn_credits`' visible, alpha or y (:145, :149, :213
    only), so once a dress of ours put it off the panel (`SettingsMenuState.CreditsHidden`) only we can bring it back:
    with `admin.settingsMenu.trim` switched off while ABOUT was on MORE GAMES (PreDress before `setup()`, its details `ABOUT
    put back`, or with both switches off its own line `settings menu: focus-in N before setup: …`; with the same-frame switch
    off the run's first frame, `settings menu: ABOUT put back after setup() (trim off)`), build 22's trim-off row (MORE GAMES
    and ABOUT at 208, as build 22 showed it); and when the panel ends on Kayac's frame 4 or the Chinese frame 5 after our
    dress of frame 3 (`settings menu: ABOUT put back (frame 4)`): whether btn_credits, at depth 69 on frames 3 and 4
    (and_settings_frames.txt), survives `setup()`'s `gotoAndStop(4)` with our writes is [I], so the fake models both. On
    Kayac the focus-in frame may then draw no ABOUT for one frame; on frame 5 (another depth) a new btn_credits is drawn
    beside `btn_more` titled ABOUT, both open ABOUT. 1.17.32 is neither (`IsKayac` comes from the server's age gate, which
    ours never sends; the Chinese GGI is not this APK's). **The hidden ABOUT's marker (found while running every suite with
    the switch on, review finding 5):** `setup()` moves the hidden real ABOUT to 208 (:213) and nothing put its marker x
    back (`Tidy` covers only buttons `setup()` shows again), so every return's PreDress found the grid not strictly in place
    and wrote it all again instead of keeping it, and an unsealed focus-in spent all four looks on the gate. A read-back that
    finds the grid in place now puts that marker back too (`Remark`: one read per marked button, a write only when it moved),
    on the run's first frame or in the look; returns are `kept` again. `IsTile` leaves `btn_more` out of the
    width check (its runtime width is about 401). The same-frame gate becomes `setup()`'s last bottom-row write,
    `btn_credits.x` 208 over our marker 0 (:213). **The tap:** `GoToFreeGames` is replaced like `GoToForum` (registered by
    `Ensure` with replace, re-ensured every 2 s). While the remap is in (`MoreGamesRemap.IsOn`: the title and the tap key on
    the same thing), `AboutRedirect.Tap` reads the focus inside the native, only while Flash work inside natives is allowed,
    and the process's first read only after its own breadcrumb (review of build 23, finding 1: the focus-in's marker is
    written once per process and always spent by PreDress before MORE GAMES can read ABOUT, and a static getter on a class
    object is a kind of call no earlier in-native step makes; `SameFrameBreadcrumb.EnterTap` has its own once-per-process flag,
    writes `<time> tap GoToFreeGames`, and posts the same two-frame delete as a focus-in, so a native fault in the read
    switches `admin.settingsMenu.sameFrame` off at the next start and the top menu decides from then on; nothing is written
    when nothing can be posted): `gluic.utils.FocusManager.componentWithFocus` (a static getter, FocusManager.as:19-22), which
    ButtonSimple gives the button it releases (ButtonSimple.as:265, :302; RELEASE :329; cleared only at :342), compared by
    ASCharacter.Equals with Settings' `btn_more`. Focus on `btn_more`: ABOUT is pushed in a posted step while Settings is on
    top (`menu_about`; a double tap finds ABOUT on top, one push); focus on anything else (the top bar's Gameloft button,
    FreemiumBar.as:566-570): the game's `UIComponent.GoToFreeGames()` at once; focus unreadable (anything but a display
    object, or an exception): the posted step decides by the top menu (Settings → ABOUT, ABOUT → not opened, else the game's).
    With the remap out the tap is the game's, with no line. Lines: `settings menu: MORE GAMES remap installed | removed | not
    installed (<why>)`; details `ABOUT on MORE GAMES (-177), ABOUT tile off the panel` or `MORE GAMES off the panel, ABOUT 208
    (<why>)` (with the switch off build 22's text exactly); `settings menu: ABOUT opened from MORE GAMES (focus btn_more |
    focus unreadable: <type>, Settings on top)`, `… ABOUT not opened (<why>)`, `… MORE GAMES tap passed to the game (focus on
    <name> | Settings not on top)`, and once per process `settings menu: focus unreadable (<type>); MORE GAMES taps decided by
    the top menu`. Kayac (frame 4) and the Chinese build (5): a return finds the panel there and is not dressed; a new panel is
    dressed on frame 3 before `setup()` moves it (above, the real ABOUT put back). Steam: unchanged
    (no remap, no registration, ABOUT at -182.9; PC's `setup()` hides MORE GAMES itself, PC-AS/MS:255).
  * **P3, F1, the sealed skip [I: nothing is measured yet].** Every gear tap builds the whole stock menu and then, in the
    focus-in frame, PreDress, about 40 calls of our GetPlatformId observer with the gate, and one look (a full read and
    tidy). After its last write PreDress now proves from reads that a look after `setup()` would change nothing: the trim on,
    the FORUM remap on and the icon out of reach (`DressedIcon` found) or no re-dress, every kept and removed instance in the
    strict read-back, and with ABOUT on MORE GAMES its remap in; ABOUT wanted but not drawn there keeps build 22's look (the
    safe side of a fallback). Only then `SettingsMenuState.PreDressSealed` names the run, and EnterSection disarms the step
    and skips the observer (in the fake: 195 Flash handles in the focus-in frame instead of 696). It is never inferred from
    `PreDressDone` (also set when the icon slot was kept). Without ABOUT on MORE GAMES, MORE GAMES' tidy moves to the run's
    first frame (`; MORE GAMES shown off the panel 1 frame`: drawn as nothing, no taps). Lines: `; same frame: not needed
    (dressed before setup)`, or build 22's `; same frame: laid out (…)` with ` (not skipped: <why>)`. PC: never sealed.
  * **P3, M, the timings (both platforms, log text only).** `SettingsMenuClock` (the instance Stopwatch API the game uses,
    PlayerComponent.cs:1172-1173 and :546, reached only through `SettingsMenuTimer.Now`, whose try/catch logs once
    `settings menu: timings unavailable (<Type>: <message>)` and leaves every line as in build 22) is made in
    `UiNatives.Start`. The focus-in line gains `; ms: EnterSection 4.2 (page 0.0, remap 0.1, grid 0.0, before setup 3.6, watch
    0.3, prompt 0.0), same frame 0.0, breadcrumb 1.9; focus-in frame dt 41.0 (the frame before 16.7); to the run's first frame
    38.0; first in this process` (dt is `GameApplication.ApplicationDeltaTime`, GameApplication.cs:569, set at each frame's
    start, :1418, so read at the run's first frame it is the focus-in frame's length, and read in EnterSection, the baseline,
    the frame before's; both include FRAME RATE LIMIT's pacing, review finding 6, so a 33 ms focus-in frame beside a 33 ms
    frame before is a paced one, not a hitch; "to the run's first frame" includes pacing too); the laid-out line `; ours 31.5
    ms over 47 frames (max 1.1 at frame 1); frame gaps median 16.7, max 38.0 at frame 2`; with both switches off one
    `settings menu: focus-in N timings (stock): focus-in frame dt X (the frame before B), to the run's first frame Y ms`. No decision reads a time. Deferred until these numbers exist: caching the reflection
    (F2), a JIT warm-up off the game thread (F3), a cheaper read-back (F4), a cheaper breadcrumb (F5).
  * **Tests** (`SettingsMenuBuild23Tests`, on fakes that now model needAlign and `txt.y` with refusals, the focus, the top
    bar's Gameloft button and the stock GoToFreeGames): 29 cases, 22 fail on build 22's code (e1bb9b9) and 7 are marked
    regression checks; `EmbeddedDllResolutionTests` walks Stopwatch's members in the trimmed assemblies. Review of build 23
    (each fails on 4130d4e's code): the first ABOUT tap's own breadcrumb and the next start switching the step off; the remap
    out once Settings is left (the OnStart window's stock `setup()` titles MORE GAMES, the Gameloft logo reads no focus);
    the real ABOUT back with the trim switched off (three ways); Kayac with frames 3 and 4 keeping their instances
    (`FakeFlashTree.KeepsSharedDepths`); the hidden ABOUT's marker (returns kept, one look); the baseline in both timing
    lines. The fake host now defaults `admin.settingsMenu.aboutCentre` to on, as shipped, so every earlier suite (returns,
    restock, another panel, the PERFORMANCE switch and the trim toggled live, the in-match gate, the renders) runs the
    phone's configuration, through about-aware checks (`SettingsMenuReturnTests.PlanOf`, `AboutOnMoreGames` on the rigs); the
    tests of build 22's bottom row itself switch it off, and `Regression_SwitchOffOrNoWorkInNatives_IsBuild22` keeps the
    switch-off case. Their Android same-frame cases use an unsealed rig.
* `admin.settingsMenu.trim` off: no relayout; with `admin.settingsMenu.performance` on, only the re-dress, in place.

### 3.3 R2's tap and R3: Performance mode and the page (`SettingsMenu/PerformancePage.cs`)

* **Mode.** Set by the `GoToForum` delegate while the tile is active, which also posts
  `MenusManager.pushMenu("menu_gameplaySettings")`. Cleared by every `EnterSection("settings_main_screen")`. Gameplay is
  pushed by pure Flash (AND-AS/MS:225-227), so the mode is off there.
* **Building the page (plan R3-B, public and dynamic members only).**
  1. In the mode `CanChangeMaxFramerate` and `CanChangeGraphicSetting` return null, so the stock rows are built. The
     `CanChangeGraphicSetting` delegate (it runs before any row exists) sets `mc_content.alpha = 0` and posts the
     transform. Review E3: that Flash write inside a native call is [G]; the transform puts `alpha` back to 1 in every
     outcome, also on the cached menu when another one is on top by then.
  2. The transform requires `menu_gameplaySettings` on top and reads every row's `name`, `type` and widget first.
  3. It keeps `MaxFramerate` (FRAME RATE LIMIT) and re-types donors: `GraphicSetting` → GRAPHICS (`mcvs.graphicsProfile`);
     the first two of `InvertCameraX`, `InvertCameraY`, `EnableAimAssist`, `DoubleTapHoldToIronSightFire` → ANTIALIASING
     (`mcvs.msaa`, 4 values) and LOW RESOLUTION MAPS (`mcvs.lowResMaps`); `CameraSmoothing` (else `CameraSensitivity`)
     → SCREEN SCALE (`mcvs.renderScale`). Never a type the menu passes as an int (`AimBehavior`,
     `AbilityButtonPlacement`, `IronSightMode`) nor one it remaps (`SprintSensitivity`, `PlayerFov`).
  4. Per row, in this order: `name` (`mcvs_<key>`) and `type`; then `values` and `mc_listSelector.dataLength`; then
     `displayList(index)`; then the slider value, mask and label; then the title (`TextUtilities.setStringID`).
  5. It removes every other row and every header, restacks with `y += height + 8` (MGS:430), calls `updateBounds()`,
     sets the title (`menu.title`, `FreemiumBar.updateTitle`) and logs `performance page: 5 rows`. Any missing donor or
     failure removes nothing, shows the content again and logs a reason (the stock rows stay usable).
* **The transform's own writes (review E1).** `List.displayList` ends with `change(old, new)`, which dispatches
  `UIEvent.CHANGE` whenever the index differs, and so does `ScrollBar.value=`. The transform runs after `onFocusIn` set
  `_isInitialized`, so every selector write runs `onListSelectorChange` → `SetUserPreference(type, Boolean(idx))` +
  `SaveUserPreferences()` (MGS:569-575), and every slider write `onBarScroll` → `SetUserPreference(type, value)` (no
  `_isInitialized` guard at all). Unchecked, an `auto` key would become the displayed value, GRAPHICS could set the
  restart flag, and a write reaching a row before its `type` changed would save a donor's **real** preference. So the
  transform raises `SettingsMenuState.Transforming` around its writes (all on the game thread); `UiNatives` and the
  page's handlers ignore every `SetUserPreference`/`SaveUserPreferences` while it is set, and also ignore a value equal to
  the one shown (the stored one, or at `auto` the engine's, §2). The `type` is written before anything that can fire a
  handler, so no write ever reaches a donor's type.
* **Writes from the player.** A SCREEN SCALE move arrives as `SetUserPreference("mcvs.renderScale", float)` (MGS:509),
  which the game ignores: our delegate applies it to the engine at once and only remembers it (a drag sends one per
  frame); the next `SaveUserPreferences` (the page's own pop, MGS:713, or a selector change) stores the last one, once.
  Selectors send only `Boolean(index)` (MGS:571), so on `SaveUserPreferences` our delegate reads `mc_listSelector.value`,
  the true index, for the three selector rows; review E2: `_firstElementDataIndex` is set before `change()` fires, so
  no post is needed.
* **Plan R3-A (spike only).** Replace the private `_arOptions` (MGS:43) inside `CanChangeGraphicSetting` with our row
  descriptors: no flash, no re-typing, if gameswf lets C# read an AS3 private member [G]. Review E6: it also needs a
  `GetUserPreference` delegate answering our `mcvs.*` keys (rows read their initial value through it; the game answers
  null for unknown keys); `PerformancePage.OnGetUserPreference` exists for that but is not registered. The first
  `CanChangeGraphicSetting` in the mode logs one spike line (can C# read `_arOptions`, did the in-native alpha write go
  through); the device log decides.

### 3.4 R3's back end (`Graphics/PerformancePrefs.cs`, small calls in `GraphicsHooks.cs`)

* **At start, in the JSON rewrite** (`GraphicsHooks.GameOptionsJson`): `renderTargetMSAA` from `graphics.msaa` (only when
  not `auto`); `maxFramerateValues` "30 60 -1" → "30 60 90 120 -1" while `admin.settingsMenu.performance` is on (read
  once at start). The boot line `[boot] graphics:` carries a `performance page: …` phrase with what was applied.
* **After `ApplyGameOptions`** (`GraphicsHooks.GameOptionsApplied`), when not `auto`: SCREEN SCALE and LOW RESOLUTION
  MAPS; then the FPS migration (§2).
* **Live** (from the page): SCREEN SCALE at once, LOW RESOLUTION MAPS for the next level, GRAPHICS and ANTIALIASING
  stored, with `RestartPending` while either differs from the value the process started with (the host's copy,
  `PerformancePrefs.HostValues`): a change put back before leaving the page asks for no restart.
* PC: every member does nothing; there `GraphicsHooks` only logs Steam's own Performance prefs.

### 3.5 R4: two rows out of Android Gameplay

* Outside Performance mode `CanChangeGraphicSetting` and `CanChangeMaxFramerate` return a boxed `false`, which wins the
  dispatch; AS never adds the two rows (MGS:229-254), so there is no gap, no header and no 'i' button. Each native has
  exactly one caller (MGS:229, :245) [V].
* **Gate (review D2, relaxed per answer 4):** `admin.settingsMenu.performance` on, no failed re-dress in this process,
  and, unless the Settings grid below is the in-match panel (frame 2), the grid shown now carries the PERFORMANCE tile. A
  main-menu visit whose grid step never ran (a dropped `EnterSection`, no post) keeps both rows, so they are never
  unreachable. The in-match Gameplay page loses them also before the first main-menu Settings visit.
* The stored FRAME RATE LIMIT keeps applying at every start: nothing touches `MaxFramerate` itself.

## 4. Tests (each names the bug it catches; all on fakes with the engine's dispatch rules unless marked real)

* **Fakes** (`tests/McvsOffline.Embed.Tests/Embedded/SettingsMenu/`): `FakeNativeTable` (registration flags, last
  non-null wins, the game's own delegates and their re-registrations), `FakeFlashTree` (frames 1-5 of both panels, the
  optionMC rows of the real `initOptions` order, CHANGE firing on `displayList` and `value=`, write order recorded).
* **WP-A `UiNativesTests`:** T-A1 a game re-registration drops ours, one ensure puts it back last; T-A2 ten ensures leave
  one delegate per name; T-A3 a throwing step answers null and dispatch does not throw; T-A4 the inactive tile runs the
  game's GoToForum exactly once, the active tile only ours with exactly one push of `menu_gameplaySettings` through the
  real page step; T-A5 without a host every delegate answers null and nothing is posted.
* **`RealSettingsMenuMembersTests`** (real Android and PC assemblies): every member used, by exact signature
  (`OverrideNativeFunction`, `GoToForum`, `ShowGenericConfirmPopup`, `CanChangeMaxFramerate`, `FpsValues`, the
  `PostProcessManager` scales and MSAA setting, `GlobalExcludeLayers`, `LowResMaps`, …). Catches a game update renaming
  anything.
* **WP-B `SettingsMenuLayoutTests`:** T-B1 the plans equal §1 to 0.01; T-B2 any one of the 12 (PC) / 13 (Android)
  lookups missing gives zero writes and the reason; T-B3 frames 2, 4, 5, another top menu or a 400-wide tile give zero
  writes; T-B4 drifted positions end identical for any drift; T-B5 both flags on every removed instance; T-B6 one
  `setup` with exactly the strings, and a throwing `setup` leaves zero further writes; T-B7 the trim and performance
  switches.
* **WP-C `PerformancePageTests`:** T-C1 the mode lifecycle; T-C2 the R4 truth table; T-C3 the transform gives exactly
  `[mcvs.graphicsProfile, mcvs.msaa, mcvs.renderScale, MaxFramerate, mcvs.lowResMaps]`, gaps of +8, no header,
  `updateBounds` once; T-C4 a missing donor or a throw halfway removes nothing and shows the page again; T-C5 an
  ANTIALIASING change stores `8x`, never `true`; **T-C5b** running the transform from all-`auto` stores nothing and sets
  no restart although every CHANGE handler ran; T-C6 `name`/`type` before anything that fires a handler, and no
  `SetUserPreference` reaches a donor type; SCREEN SCALE applied live and stored once per visit, whatever order the saves
  run in; SCREEN SCALE at `auto` shows the match scale and a write of that value is no change; the restart prompt once,
  Yes restarts, No keeps.
* **WP-D `PerformancePrefsTests`** (the real `GameOptions_android.json`): T-D1 msaa `auto` byte-identical, `4x` in the
  block the engine resolves for every profile; T-D2 the list rewrite skips "23 60 -1"; T-D3 the migration once, the way
  back, nearest-lower mapping, VeryLowGPU untouched, idempotent without the marker; T-D4 our scale wins over the
  profile's (0.5 → 0.7), `auto` leaves it; T-D5 lowResMaps `on`/`off` from every start state, `auto` changes nothing;
  T-D6 GRAPHICS to the same value raises no restart.
* **Store** (`SettingsMenuContractTests`): validation, never on the calling thread, a failed save logged and rolled
  back, two out-of-order saves of one key leave the latest.
* **Build 20 `SettingsMenuReturnTests`** (the fake host runs frames as `UpdateWorlds` does: the context drain, the
  `DelayedPost` items, then the Flash frame; the fake tree's `setup()` restocks the grid after `EnterSection`, Steam
  places new instances, and two switches model a popped submenu still on top for K frames and the stock grid coming back
  K frames after the return): the grid after the first entry and after every return (each tab, PERFORMANCE,
  ABOUT/HELP/LANGUAGES/ACCOUNTS, the restart prompt, after a match) on both platforms; one line per run; a re-stock laid
  out again; a newer focus-in superseding the run; `TileActive`/`RedressFailed` across retries and the R4 gate; the
  `focus-in` diagnostic line; Back during the wait. 23 of the first 26 fail on build 19's step (the rows of the gluic
  order pass on it too, §3.2), the review's 7 on the code before the review. The tests of the run's own rules run with the
  same-frame step off (`Rig(steam, sameFrame: false)`).
* **Build 21 `SettingsMenuRenderTests`** (the fake host's `RunFrame(input)` runs the frame's tap or Back inside the Flash
  update and then a render point that records what is drawn; the fake `setup()` calls the AS natives in the AS order:
  `GetPlatformId` through every setStringID, 3 per label on Android and 2 on Steam, the 4/6 tests and `addButtonListeners`
  after the bottom row, Steam's `GetLanguage` at `:367` and adjustToViewport's before EnterSection, FreemiumBar's title after
  STACK_CHANGED): no render with Settings on top draws anything but the trimmed grid, after the first entry, every tab and
  Back (PERFORMANCE and ABOUT included), the page with the restart prompt, and after a match (the UI's natives registered
  again), on both platforms; the game still gets its own platform and language. All 7 fail on build 20 (one stock render
  per focus-in). **`SettingsMenuSameFrameTests`**: the focus-in and laid-out lines say which path laid the grid out; the
  observer only next to the game's delegate and only until the next frame; no call of the native outside a Settings
  focus-in frame reaches the step; another section, the in-match panel and the trim off arm nothing; Android looks once, at
  `addButtonListeners`, and its own re-dress is not looked at; Steam looks at every armed `GetLanguage`; a step or a write
  that throws reaches no Flash and leaves it to the run; at most 4 looks when the grid cannot be read, and Back still ends
  the run; a second focus-in in the same frame replaces the step. `RealSettingsMenuMembersTests` adds
  `RemoveNativeFunctionOverride(object)`, `UIComponent.GetPlatformId() : int` and `MenuComponent.NativeGetLanguage() :
  string` on both platforms.
* **Build 23 `SettingsMenuBuild23Tests`** (§3.2, "Build 23"; the fake tile has BtnSimple's needAlign and centres `txt.y`
  while it is true, the fake `setup()` turns it off before each tile's setup, and the host's needAlign or `txt.y` write can
  throw or be ignored; a FocusManager set by a press and cleared after the RELEASE; the top bar's Gameloft button; the
  game's own GoToFreeGames from AddBrowserOverrides): the PERFORMANCE title at 294.2 on every gear tap with and without
  the remap and through the undress, put back when needAlign is refused or not taken, one line when neither write takes and
  nothing else changes; one ABOUT drawn at (-177, 354.1) on every render of five gear taps and two returns from ABOUT, also
  sampled between the dress and `setup()`; its tap opens ABOUT, the Gameloft logo still runs the game's GoToFreeGames, an
  unreadable focus opens ABOUT while Settings is on top with one line, a double tap one push, a failed dress or read-back
  build 22's row without the remap, two ABOUTs both open ABOUT, the stock GoToFreeGames back from OnStart repaired, a
  language reload; F1's seal (no observer, fewer Flash handles) and its refusals; the timings with a fake clock, a failing
  clock and both switches off. 29 cases, 22 fail on e1bb9b9 and 7 are regression checks (Kayac and the match panel, the
  switch or the same-frame switch off, Steam).

## 5. Risks and what only a device can tell

1. **Registrations dropped** between ticks if a component restarts while Settings is open [I]: a tap in that window gets
   stock behaviour (FORUM forwards to the game). The log counts re-registrations; the fallback is the IL trigger (§3.1).
2. **One-frame flash** of the stock grid (or FORUM/MORE GAMES) on each focus-in, since the posted step runs at the start of
   the next frame: seen by the owner on build 20 (no menu-in transition covers it, §3.2). Build 21 lays the grid out inside
   the native that follows `setup()`'s last stock write (§3.2, "Build 21"); the owner still saw MORE GAMES and FORUM flash
   on the S22. Build 22 makes Android's `setup()` writes draw nothing (§3.2, "Build 22"), so on Android the flash no longer
   depends on the look landing; what is left [G, device]: which way gameswf resolves `this.mc_image` (the line says
   `icon slot null` or `kept`; kept means the icon is re-dressed by the look, as in build 21, and can blink while it
   reloads), whether a setter on the timeline-placed `mc_image`'s name is refused (then the slot alone must hold), and
   whether the remap's dictionary is the client World's formatter's (the line says `FORUM remap installed`). The first
   entry is dressed after a `gotoAndStop(3)` of ours; an Android build with the Kayac or Chinese frames would then go to 4
   or 5 in `setup()` (new instances) and fall back to build 21's path (1.17.32 is neither). Steam keeps build 21's step:
   if the device log shows `same frame: not watched` or `no look` there, the fallback is the IL hook in `UpdateWorlds`
   before `UpdateSignal.Set()` (a new fingerprinted patch). **Our Flash work inside natives** (PreDress, the look) could
   meet a native fault no managed code can catch: then `admin.settingsMenu.sameFrame` turns itself off at the next start
   (the breadcrumb, §3.2) and the owner can switch it back on to retry. False positives need the process to end inside
   those two frames for another reason (a crash of the game in the rest of the frame or the next, or Home within about
   33 ms of the tap or Back and the app swiped away before it drew twice more); normal exits, backgrounding at any other time and watchdog
   reports leave no marker.
11. **ABOUT is not centred on Android** (build 22, by design, §1.2): it stays at `setup()`'s own 208, as ThinBtn's
    timeline moves its children on every button state. Build 23 draws it on MORE GAMES' button at -177, 5.9 units (about
    5 px) right of the plate's true centre; exact centring needs a write after `setup()`.
12. **needAlign's setter** (build 23, P1) [G]: the read-back tells a write that made a dynamic member beside the accessor
    (`needAlign not taken`); the repair then writes `txt.y`, and whether gameswf keeps a script-written `txt.y` across the
    tile's own timeline states is [G] (the line says `title y put back` or `not put back`).
13. **FocusManager's static getter read from C#** (build 23, P2) [G]: unreadable means the top menu decides, so a Gameloft
    logo tap in the top bar while Settings is on top opens ABOUT (harmless offline; logged once). A native fault in the read
    itself cannot be caught: the process's first read has its own breadcrumb (§3.2, "Build 23"), so the next start switches
    `admin.settingsMenu.sameFrame` off, which stops the read; later reads in that process have none (as later focus-ins).
14. **The stock GoToFreeGames back for up to 2 s** after OnStart: EnterSection repairs it before the dress; a MORE GAMES tap
    in that window runs ShowMoreGames, a no-op offline. With the MORE GAMES remap out whenever Settings is not in focus, a
    stock `setup()` in that window titles MORE GAMES, so its title and tap agree. ABOUT also opens one frame later than a
    stock ABOUT tap (posted, as the PERFORMANCE tile).
15. **The seal** (build 23, F1) [I]: it rests on reads after the last write and on `setup()` writing nothing a look would
    change; if a future game build adds such a write, the look must come back (the seal's conditions name the plan).
16. **Stopwatch in the trimmed BCL** (build 23, M): walked in the trimmed assemblies by a test; any failure on the phone is
    one line and build 22's text.
17. **Kayac and Chinese frames after our frame-3 dress** (build 23, P2) [I]: whether gameswf keeps the instances frames 3
    and 4 share by depth (btn_credits at 69) across `setup()`'s `gotoAndStop(4)` is not known; if it does, the run's first
    frame puts the real ABOUT back (one focus-in frame may draw none). Not this APK (§3.2, "Build 23").
3. **Hidden tiles taking taps:** `visible=false` + `mouseEnabled=false`, as Gameloft hides MORE GAMES itself (PC-AS/MS:255)
   [I strong]. On PC, keyboard and gamepad focus must not reach them either: nothing of ours changes focus navigation, so
   only the device check can tell [G].
4. **Rows with no visible effect:** SCREEN SCALE and ANTIALIASING act only when the profile renders the 3D scene through
   a post-process target [G for Mid]; 8x MSAA on Adreno 730 [G]; FPS above 60 [G]. Review C1: on a 120 Hz Samsung panel a
   90 or 120 limit may still deliver 60, because the engine's limiter only throttles and the display mode is the OS's
   (adaptive refresh, Game Optimizing Service) unless the app asks for a higher rate. If the S22 stays at 60, answer 3
   falls back to 30 / 60 / UNLIMITED.
5. **Re-typed rows inherit the generic widget:** the slider shows "%", selectors send a Boolean (covered by the index
   read). If a future game build changes the row widget, our checks abort to the stock page.
6. **FRAME RATE LIMIT is unreachable in a match** (answer 4).
7. **Restart on Android 13+** may only close the game (`AndroidExit`); the choice is saved either way.
8. **Process-wide statics:** LOW RESOLUTION MAPS and SCREEN SCALE also reach the in-process match server Worlds, as on Steam.
9. **The optional `forum` patch** is reachable only with both switches off on Android (with performance on, FORUM is the
   PERFORMANCE tile), and with the trim off on PC. It stays; retiring it is a later cleanup.
10. **A SCREEN SCALE drag left without any save** (the process killed while the page is open) is lost and comes back at
    the next start, as the game's own slider preferences are.

## 6. Device checks

* **Phone (S22):** the grid (centring, slow-motion video for the flash); taps on the hidden-tile areas do nothing; every
  tile opens its page and PERFORMANCE opens the page, Back returns to the trimmed grid every time (build 20: each tile
  twice, and the `settings menu: focus-in` and laid-out lines of each return); Gameplay shows neither row, in the main
  menu and in a match; each Performance row changes the engine (the `[boot] graphics:` line, a visible scale change); the
  restart prompt; FPS 90/120 measured on the 120 Hz panel; the R3-A spike line. Build 21: no stock grid on any return
  (slow-motion video), and per return the `focus-in` line's `same frame: …` part and the laid-out line's `in the focus-in
  frame; stock grid drawn 0 frames`. Build 23: the PERFORMANCE title under its icon at the other tiles' height on five gear
  taps (pressed, it shrinks like the others: needAlign worked), one ABOUT centred on the bottom row that opens ABOUT, the
  top bar's Gameloft logo as in build 22, `same frame: not needed (dressed before setup)` and the `ms:` parts of the lines
  (every tap or only the first after start; the FRAME RATE LIMIT), `admin.settingsMenu.aboutCentre` off giving build 22's row.
* **PC:** the grid at 1920×1080 and at a 16:10 size, also after Back from every tile (build 20, with the same log lines);
  PERFORMANCE still opens Steam's page; build 21: no stock grid on any return, `same frame: laid out (GetLanguage calls N,
  looks 1)` and `stock grid drawn 0 frames` in the log; hidden tiles cannot be reached
  with the mouse, the keyboard or a gamepad; no drift of ABOUT after five submenu round trips.
* **Per new game build:** a static check of the exported AS (the panel instance names per frame, the last native call in
  `MenuSettings.setup()`, `CanChangeMaxFramerate` before `CanChangeGraphicSetting`, the int-passing type list in
  `onListSelectorChange`), kept outside the repo with the exports.
