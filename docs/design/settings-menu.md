# The game's Settings menu: the trim and the Android PERFORMANCE page

Status: built in build 19 (`hosts/embedded/Admin/SettingsMenu/`, `hosts/embedded/Graphics/PerformancePrefs.cs`); not
yet seen on a phone or on Windows. This is the final design: the review corrections (A1-A3, B1-B2, C1, D1-D2, E1-E7, F1
of the design review) are folded in, and each place says which one it is.

Tags: [V] checked in the exported ActionScript, the decompiled C#, the game data or the repo code. [I] inferred from the
code, not run. [G] guess (a device check decides).

References to the game's own code (not in the repo; they are game data):

* `AND-AS/`, `PC-AS/`: the exported ActionScript of the Android (1.17.32) and Steam (1.17.30) `UI.swf`.
  `MS` = `menus/settings/MenuSettings.as`, `MGS` = `menus/settings/MenuGameplaySettings.as`.
* `AND-CS/`, `PC-CS/`: the decompiled C# of the Android and Steam assemblies.

## 0. Requirements and decisions

The owner's requirements (October 2026) and his answers to the five open questions (all five: the recommended default):

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
| bottom | ABOUT (`btn_credits`) | -182.9 | 354.1 |
| hidden | `btn_social`, `btn_customer`, `btn_more` | | |

* **Order.** The PC order with CUSTOMIZE CONTROLS after AUDIO, so both platforms read the same way and PERFORMANCE
  comes last, as on Steam.
* Row 1 keeps the stock columns, already centred (S22 screen x 152-1849) [V]. Row 2 keeps row 1's pitch (about 477.4)
  and is centred on row 1's centre [I].
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
  thread). The settings page shows and edits the same keys. The game's LocalStorage is not used: the game drops unknown
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
  stored with `RestartPending` when the value changes (none when it is the same).
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

## 5. Risks and what only a device can tell

1. **Registrations dropped** between ticks if a component restarts while Settings is open [I]: a tap in that window gets
   stock behaviour (FORUM forwards to the game). The log counts re-registrations; the fallback is the IL trigger (§3.1).
2. **One-frame flash** of the stock grid (or FORUM/MORE GAMES) on each focus-in, since the posted step runs at the start of
   the next frame [I]. The page is covered by `alpha=0`; the grid relies on the menu-in transition. If visible, a scoped
   synchronous variant (Flash writes inside the native) is the next step [G re-entrancy].
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
  tile opens its page and PERFORMANCE opens the page, Back returns to the grid; Gameplay shows neither row, in the main
  menu and in a match; each Performance row changes the engine (the `[boot] graphics:` line, a visible scale change); the
  restart prompt; FPS 90/120 measured on the 120 Hz panel; the R3-A spike line.
* **PC:** the grid at 1920×1080 and at a 16:10 size; PERFORMANCE still opens Steam's page; hidden tiles cannot be reached
  with the mouse, the keyboard or a gamepad; no drift of ABOUT after five submenu round trips.
* **Per new game build:** a static check of the exported AS (the panel instance names per frame, the last native call in
  `MenuSettings.setup()`, `CanChangeMaxFramerate` before `CanChangeGraphicSetting`, the int-passing type list in
  `onListSelectorChange`), kept outside the repo with the exports.
