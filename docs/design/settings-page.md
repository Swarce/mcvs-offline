# Design: the "MCVS Offline" settings and diagnostics page

Status: **implemented in build 9, awaiting its device test** (§12). Designed as revision 2 (October 2026, after
build 8 / device test 7 and two design reviews); the sections below are updated to what was built, and "As built" lists
every change against revision 2. Roadmap item "Settings and diagnostics page" ([ROADMAP.md](../ROADMAP.md)). This
document is the contract between the work packages in §10: anything another package relies on (routes, JSON shapes, C#
signatures, DOM ids, file layouts, text formats, IL fingerprints) is fixed here. Change it only through the integrator
(§10.1).

Evidence: the research notes `polish-research.md` (sections *server-surface* and *android-ui*) and the code as of
`main-dev` `e4bff79`. Decompiled game code is quoted by file and line from `/home/user/mcvs-work/decompiled*`. IL
fingerprints in §9 were computed with the algorithm of `IlPatch.Fingerprint` on the player's 1.17.32 assemblies
(`Game.dll` sha256 `e870eb07…`, `Gameloft.Engine.dll` sha256 `af999595…`); the computation reproduces every fingerprint
already in `patcher/Patches`, exception-handler bodies included.

## Revision 2: review findings and where they are resolved

| Finding (severity) | Resolution | Section |
|---|---|---|
| Paste import cannot carry a real save (557 KB–1.4 MB) (blocker, both reviews) | A compact **transfer text**: raw deflate + base64, cut into self-delimiting, CRC-checked parts (default 60,000 characters; 3,900 for Telegram, 1,900 for Discord), collected on the server across pastes; exported as a `.txt` file or copied part by part. Measured on real profiles: 358 KB → 43 KB, 591 KB → 107 KB of text. A table says which import path works on which Android version. | §6.2, §6.8, §4.6 |
| The per-boot news ticket is a bearer secret readable with one unauthenticated `GET configs/users/me` (major) | **No secret anywhere in the CRM document, the profile or a file.** The inbox link is a fixed marker URL (`…/_mcvs/ui/#news`); a new default patch `adminlink` (prefix on `BrowserComponent.OpenUrl`) recognises it and opens the page with a fresh one-time ticket. Without the patch the marker opens a view-only page. The boot ticket is removed. | §5.3, §9.1, §9.3 |
| `admin.openVia=post` is refused by the gate's own CSRF rule (major, both) | An exact exemption for `POST /_mcvs/ui`; the answer is `303 See Other` to `GET /_mcvs/ui/` with the cookie (a refresh never re-POSTs; WebView's default `onFormResubmission` refuses to resend); `SetParameters` before `OpenURLWithPostData`. | §5.2, §8.3 |
| `storage.backups.keep = 0` disables pre-import backups and makes `CreateBackup` prune everything (major) | `keep` governs only `auto` backups; every other reason is always backed up; pruning is per pool and never runs with 0. | §6.3 |
| Pinned backups evict manual ones; the on-disk reason is `preimport`, not `pre-import` (major) | Four pools: `auto` (keep), `event` (20), `system` (`preimport`, `prerestore`, `presettings`, `prerevert`; 10), `manual` (10, never deleted automatically: an 11th is refused). On-disk tokens fixed. The apply-time backup is skipped when the file is unchanged since staging. | §6.3, §6.4 |
| "Restart now" loses unsaved progress and fights Android's own relaunch of the activity under the browser (major, both) | A **save-then-exit sequence**: close the browser, wait for the game loop, ask the game to save (`ForceSaveProfile`, the game's own RPC), wait for that save to arrive, then exit through the game's own restart path (`MainActivity.a(true)`, which needs the activity resumed: true once the browser is closed). A restart without a save needs explicit consent. Platform-independent order, unit-tested. | §4.5, §8.8 |
| No in-game way back after a `switch` import (major) | A staged `relink` operation ("Play this account on this phone") for any local account, an **Undo** of the last applied operation, and `switch` offered for backups of other accounts. | §6.4, §6.7 |
| Import validation accepts any JSON object (major) | Strict checks are errors (profile marker, non-empty `_mcvs.gameProfile`, account id format and agreement, versions); an explicit confirmation naming the plan. | §6.1, §4.6 |
| No recovery when an applied save stops the game reaching the menu (major) | **Probation**: cleared by the TS's first save of the target (every TS login saves, `TakeProfileLock`); after two more starts without it the operation is reverted automatically before the port opens. | §6.5 |
| DNS rebinding and web-page abuse of the Federation routes; redeem trigger fired by anyone (major) | The Host allowlist applies to **every** route while the listener is bound to loopback and LAN mode is off (file-only kill switch); the redeem trigger needs loopback, a token for the current player, no browser open, at most once per 30 s. | §5.2, §9.5 |
| Landscape geometry assumed 320–480 px; the browser leaves 250–330 px (major) | Designed for 250–330 px: no page top bar, a non-scrolling action bar, steppers and selects over typed fields, dialogs that scroll inside, system font scale respected. | §2.1 |
| The logs zip (25–45 MB stored) is too big for Discord and Gmail (major) | Deflated entries, default scope "recent" with a hard 8 MiB cap, build first and show the size, then share. | §7.2 |
| Minor findings | Idempotent apply with recorded steps (§6.4); strict identifiers (§4.2); current player from the game's own credential (§6.6); one redaction pipeline for every logger (§5.4); header-time body limits and ticket-first rate limiting (§5.2, §5.3); masked settings file, backup before `force` (§4.4); backups before risky settings (§3.1); ticket handling on activity re-creation (§2.5); JNI fallbacks exercised deliberately (C3); inbox works when the native browser is not initialised (§9.1); WebView UX and Back guard (§2.1); share targets and the freezer (§8.5); Download pruning and `CanSaveToDownloads` (§8.7); wording for loop-dependent actions (§2.2); one JNI table, checked against `android.jar` and the APK's dex (§8.6); package ownership (§10); apply duration budget (§6.4); the target SDK as a tested invariant (§0: I1, since replaced by I1'). | as listed |

Not taken: "accept restart only while `PageAlive` was seen in the last 15 s". It defended against a second holder of
the news ticket; with no multi-use ticket left, a session exists only in the game's own WebView, and the restart request
itself is a `PageAlive`. "Never evict a ticket session in favour of another": every session now comes from a one-time
ticket only the WebView receives (§5.3).

## As built (build 9): changes against revision 2

Found while implementing and reviewing the packages and at the integration (WP8). The sections named are updated.

| Change | Why | Section |
|---|---|---|
| The redeem code needs **no** access token; a token that is present must be the current player's. New checks (`GameRequest`, `server/Core/Http`): no `Origin`, `Referer` or `Sec-Fetch-*` header; the client id in the path has the game's form (`<digits>:<digits>:<version>:<platform>:<store>`); the `User-Agent` is exactly the game's `[<agent> ](<client id>; <datacenter>)` for it (no `Mozilla/`). The code does nothing while `lanMode` is on. | The game's coupon request has no token (`AssetsApi` builds it without scopes; `FedRequestMessage` adds a token only to scoped requests), and without the header checks any web page on the phone could send the GET (an `<img>`, a no-cors fetch). Review: the path's client id is the caller's choice, so "contains `(<client id>; `" let an old browser's `<img>` pass with `coupons/Linux/…` (every Android UA has `(Linux; `). In LAN mode a guest's redemption runs on this phone's menu server and reaches the Federation exactly like the player's own. | §9.5, §5.5, §10.8 |
| The current player's `session` source counts only the game's own login: `auth/authorize` marks a direct anonymous grant as the player's (`TokenService.Issue(…, playerLogin)`, `LastDirectAnonymous`) only for a POST from loopback with a device id, the game's `User-Agent` and no browser header; the resolver also requires the grant's device id (equal to the host's when it knows it). | Review: `authorize` is served on GET and takes any anonymous name without a password, so a blind `<img>` to `127.0.0.1` without a device id made its own account "the current player" whenever the game's credential was unknown (always on the standalone host). | §6.6, §10.2 |
| Back guard: the page takes its history entry off again (`history.back()`, ignoring the `popstate` it causes) once no dialog is open and nothing is unsaved; every event handler and every API answer re-checks it. The probation banner shows the server's sentences as written (no repeated "Reverted automatically:", no ".."). | Review: the entry stayed after a dialog, a Save or a Discard, so the next hardware Back did nothing visible (`InGameBrowser.onBackPressed`: `goBack` while it can, else close). | §2.1, §2.3 |
| Post mode: the redirect always carries a screen (`#s=status` by default). | Review: a failed exchange redirected to a bare `/_mcvs/ui/`, which the page shows as view mode instead of `#banner-nosession`. | §5.3 |
| The CRM warm-up waits up to 2 s for `IAdminPortal` while `crm.newsfeedLink` is on (requests do not wait). | The portal registers after the CRM module; without a data pack the warm-up built its document first and the TS's first request rebuilt it inside its 1 s `WaitForCRM`. | §9.1 |
| Probation only for an operation after which the phone's login opens the target: a `replace`, or a switch or relink that moved the login. Not `restore-other`, a skipped relink or any undo. History entries carry `"probation": true/false`; results are `applied`, `failed`, `confirmed`, `reverted`, `superseded`. | A save of an account the phone does not load never comes, so a correct operation would be reverted two starts later; an undo's revert would redo the operation. | §6.4, §6.5 |
| `<id>.payload.json` also for restores and undos (the backup text, copied at staging); the stage-time backup's pruning spares the backup being restored; relink objects are `{credential, from, to}`; a resumed apply reads the relink outcome back from the accounts. | Pool rotation could delete the backup a staged restore needs before the restart. | §6.4 |
| 413 `too_large` of an export carries `details: {limit, unit: "parts"\|"bytes", partChars}`; 409 `busy` / `restarting` from every saves route that takes an account lock while a save-now or restart runs; the validation result adds `errorCodes` (e.g. `current_player_unknown`), `message`, `set`, `rejected`; separate rate-limit buckets (export 6/min, logs zip 6/min, logs share and text report 6/min, paste 30/min). | The page needs the details to offer larger parts; a request blocked on the account lock held by a restart would hang its connection. | §4.1, §4.6, §5.3 |
| A raw profile file (pasted, or from the import folder) is held to the bundle's 12 MiB profile limit. Exports and zips are pruned by write time and never the file just written. | Export names start with the alias, so names do not sort by time. | §6.1, §4.6, §7.2 |
| `storage.backups.keep` caps only `auto`; the interval check (`IsDue`) counts only `auto` backups. | Other pools must not delay the next automatic backup. | §6.3 |
| Post mode keeps the URL's screen and result: the host posts `t=<ticket>[&s=<screen>][&r=nosave]`, the server redirects to `/_mcvs/ui/#s=…&r=…` (values checked as the page checks a fragment). | The reopen after "Save the game now" landed on Status without its "did not save" toast. | §5.3, §8.3 |
| Host details: UI-thread calls are bounded and withdrawn when not started in time (`UiCall`); thread dumps and ANR traces are mirrored again while they grow; the watchdog's `paused`/`resumed` lines are INFO. | Review of WP5. | §7.5, §8.5, §8.6, §8.8 |
| Integration: `ServiceRegistry.RegisterAll(router, context, include)` registers a subset of the modules (tests); the standalone console prints the page URL; `TokenService.RevokeCredential` logs the masked credential (`Credential.Mask`, the form of `AdminApi.MaskCredential`); `HttpStatus` has 303, 415 and 429. | | §8.9, §8.10, §10.9 |

## Additions after build 9: graphics, HD textures, notifications

| Change | Why | Section |
|---|---|---|
| New group **Graphics** (after Gameplay): `graphics.profile` (enum auto/mid/high/ultra, restart), `graphics.fullProfile` (bool, restart, confirmed on enable), `hd.enabled` (bool, restart), `hd.mount` (enum auto/save/cache/asset/front, restart). Advanced (read-only): `notifications.request`, `notifications.requestedAt`. | docs/research/pc-textures.md WP0, WP1/WP3; target-sdk.md WP-T5. All are read by the embedded host at boot (patch `graphics` hands it the GameOptions text once per process), so they apply after a restart. | §3.1, §3.2 |
| `AdminHostInfo` gains `RelaunchUnconfirmed` (Android 13+), `Notifications`, `Graphics`, `HdTextures`; `GET status` returns them as `host.relaunchUnconfirmed`, `host.notifications`, `host.graphics`, `host.hdTextures`; Status › Game host shows the three rows. | The status row of the notification permission, and what the engine really got. | §4.3, §8.1 |
| Build 11: `hd.mount` is gone (the zip mount was removed); `hd.enabled` installs the HD pack `McvsOffline/hd/mcvs-hd-*.zip` into `Data/` (false: removes it at the next start). `AdminHostInfo.HdInstalled` (`host.hdInstalled`); Actions › **Remove HD textures** (shown while it is true and `hd.enabled` is not false) asks first, posts `hd.enabled` false through `POST settings` and offers the restart. | [design/hd-install.md](hd-install.md); a restart is what puts the originals back (the installer step runs only at start-up, before the game's installer). | §4.3, §4.5 |
| On Android 13+ (`host.relaunchUnconfirmed`) every Restart text (Actions, the restart dialog and its toast, the restart banner, Status' "Restart via") says the game may close without coming back and then must be started again; none says it will not come back. | Since Android 13 AlarmManager is expected to start no activity for an alarm, at any target (target-sdk.md §4.6, critic note 3), but STATUS device-test item 9 has not been observed on its own (device test 8 passed checks 1–13 only as a whole). Reword to a firm "closes" only after item 9 or WP-T7 shows no relaunch. | §4.5, §8.8 |
| Build 19: the game's Settings menu ([design/settings-menu.md](settings-menu.md)). Gameplay: `admin.settingsMenu.trim` (bool, true, live; both platforms) and `admin.settingsMenu.performance` (bool, true, live; Android only, hidden on PC). Graphics (Android only, hidden on PC): `graphics.msaa` (enum auto/off/2x/4x/8x, restart), `graphics.renderScale` (text `auto` or 0-100, live), `graphics.lowResMaps` (enum auto/on/off, menu). Advanced (read-only, Android): `graphics.fpsListVersion`. | The trimmed Settings grid on both platforms and the Android PERFORMANCE page; the game's PERFORMANCE page and this page edit the same keys, so a bad value can be fixed here. | §3.1, §3.2, §9.4 |

## 0. Decisions already taken (not reopened here) and invariants

| # | Decision | Consequence for the design |
|---|---|---|
| D1 | The page opens in the game's **own in-game browser** (`com.gameloft.ingamebrowser.InGameBrowser`, a WebView activity in the game process and task), through `World.Browser.OpenUrl(url, true)` on the game thread, with a JNI `InGameBrowser.SetParameters` + `OpenURL` fallback. | The game stays the foreground app (no cached-apps freezer). The game loop is paused while the page is shown: nothing the menu server does (saves, CRM refresh, unlock grants) happens until the page closes. |
| D2 | **Chrome is unsupported for writes.** The freezer suspends the game ~70 s after it leaves the foreground (Android 14+ also closes its sockets). | No ticket is ever handed to Chrome, so Chrome never gets a session. Read-only `_mcvs/*` diagnostics stay reachable from Chrome on the phone. |
| D3 | URLs use **`127.0.0.1`**, never `localhost` (the game's `network_security_config` allows cleartext only to `127.0.0.1` and `gameloft.com`). | Every URL the server hands out for the page is `http://127.0.0.1:<port>/_mcvs/ui/...`. |
| D4 | The WebView has JavaScript, **no file chooser, no downloads, no `localStorage`/`sessionStorage`, no JS bridge**, ignores the viewport meta tag (wide viewport off), shows a loading dialog per page load and has `setSupportZoom(true)` without `setTextZoom`. | One self-contained ES5 page, XHR only, all state in JS memory or on the server, no `<input type=file>`, no `download` links; files leave the phone through the Android share sheet driven by the host over JNI; files enter it as text (§6.2) or through `McvsOffline/import/`. |
| D5 | Triggers: the CRM `newsfeeds` inbox item first, then a Settings › Account "MCVS Offline" button (two Game.dll hooks), optional Forum-button repoint and a hidden redeem code. | §9. Revision 2: the inbox item carries no secret; it opens the full page through the `adminlink` hook (Gameloft.Engine.dll) and a view-only page without it. |
| D6 | Security: loopback + Host allowlist, one-time ticket in the URL **fragment** exchanged for an `HttpOnly; SameSite=Strict` cookie, writes need JSON + a custom header + an Origin check, a settings key allowlist, tickets are never logged, the Host check is added to the existing `_mcvs/*` routes. | §5. Revision 2 extends the Host check to every route while bound to loopback. |
| D7 | Logs: the 0-byte uploads come from MediaStore rows left `IS_PENDING=1` / without `_size` (FUSE direct-path writes are never scanned; in-place `O_TRUNC` rewrites). Fix the mirror (temp file + rename + scan) and add "share logs" (zip in MediaStore Downloads + `ACTION_SEND` over JNI; text/clipboard fallback before Android 10). | §7, §8.6, §8.7. |
| D8 | Saves: list/create/restore backups, export (share sheet), import by paste, from `McvsOffline/import/` or from backups; imports and restores are **staged and applied at the next start** (the running menu server holds the profile in memory and saves it back). Account id = SHA-1 of the native anonymous credential, so an import from another phone **relinks the current credential** to the imported account. | §6. Revision 2: paste carries the compact transfer text; relink and Undo are staged operations too; applied operations are on probation. |
| D9 | Settings: **two `Settings` instances** stay (host copy `EmbeddedHost.Settings`, server copy `ServerContext.Settings`); no reload. The page writes the **server copy** (`Set` + `Save`); every key is classified `live` / `menu` / `restart`. A broken `settings.json` is shown with an explicit "move aside and reset" action. Optional "restart the game" action (the game's own restart mechanism). | §3, §4.4, §8.8. Revision 2: a restart always asks the game to save first. |

**Invariants** (every package keeps them; tests named in §10 guard them):

* **I1' — target SDK** (replaces I1, "target SDK 29"; normative text: [ARCHITECTURE.md](../ARCHITECTURE.md) §2 and
  [research/target-sdk.md](../research/target-sdk.md) §6). The patched APK targets 35 by default (`targetsdk` patch;
  `--target-sdk 29` is the fallback and gives the shipped manifest and layouts, `AndroidManifest.xml:13`). The design
  relied on 29 for five things; at 35: the game's own restart `PendingIntent` without `FLAG_IMMUTABLE`
  (`MainActivity.c()`, flag 32768) needs the `classes.dex` edit P1, which build 10 makes in the same run (the patcher
  refuses 31+ unless `classes.dex` was edited in that run); the share chooser and the StrictMode
  `file://` share on Android 7–9 were never target-bound; the relaunch alarm is blocked on Android 13+ at any target;
  the browser activity's edge-to-edge is handled by `fitsSystemWindows` on both layouts. `ManifestTargetSdkTests`
  asserts it (the target, `exported`, `POST_NOTIFICATIONS`, `<queries>`, the layouts, no Google Analytics components,
  and the refusal without the dex edits: `ATarget31OrMoreNeedsTheDexEditsOfTheSameRun`, a unit test of
  `ApkPatcher.CheckDexEdits`); `RealDexTargetSdkTests` asserts the dex side.
* **I2 — no stored secret.** No ticket, cookie or other bearer secret is ever written to a file, the CRM document, a
  profile, a log line or a share. The only secrets are one-time tickets (memory, ≤ 120 s, URL fragment or form body of
  the game's own WebView) and session cookies (memory and the game's private WebView cookie store).
* **I3 — the running menu server owns the current profile.** The page never writes the profile of the account the game
  is playing; it stages, and the change is applied before the port opens at the next start.
* **I4 — nothing deletes a manual backup** except the player's own Delete on the page.

Non-goals (v1): Chrome or PC browsers writing on Android; editing network/identity keys from the page; importing
leaderboard history or social data from another phone; a FileProvider or any manifest/resource change; an Android
storage picker (needs a Java `onActivityResult` plugin); the Steam "open" action (§8.10 only notes how it plugs in).

## 1. Architecture overview

```
 game process (com.gameloft.android.ANMP.GloftMVHM)
 ┌───────────────────────────────────────────────────────────────────────────────────────────────┐
 │  InGameBrowser (WebView)  ── XHR ──►  HttpServer 127.0.0.1:51780 (connection threads)          │
 │   /_mcvs/ui/#t=<ticket>                 │ header-time body limit (WP1)                         │
 │                                         ▼                                                      │
 │                 McvsServer.Handle ── LocalAccessGate (WP1, every route) ── Router              │
 │                                                     │                                          │
 │        ┌────────────────────────────────────────────┼──────────────────────────────┐           │
 │        ▼                                            ▼                              ▼           │
 │  admin (WP2): page, session, status,   admin-saves (WP3): backups,      admin-logs (WP3):      │
 │  settings, actions, ExitSequence,      transfer text, staged ops,       tail, zip, share       │
 │  GameSaveTracker, IAdminPortal         probation (apply at boot)                               │
 │        │  uses: Settings (server copy), AccountStore, TokenService, LocalProfileStore,         │
 │        │        ProfileBackups, IProfileAccess, DocumentStore("messages"), CrmDocumentCache    │
 │        ▼                                                                                       │
 │  IAdminHost (context.Features)  ◄── implemented by hosts/embedded/Admin/AdminHost (WP5)        │
 │        │  open page · close browser · game save · exit · share · clipboard · toast · lease     │
 │        ▼                                                                                       │
 │  Gameloft.Engine (reflection, game thread) / JNI (Mono.Android JNIEnv, UI thread)              │
 │                                                                                                │
 │  Triggers: Settings › Account button (adminpage, Game.dll) · inbox marker link (CRM WP7 +      │
 │            adminlink, Gameloft.Engine.dll) · Forum (forum) · redeem code (WP7)                 │
 │            → McvsOffline.AdminHooks (WP5) / IAdminPortal.OpenPage → IAdminHost.OpenPage        │
 └───────────────────────────────────────────────────────────────────────────────────────────────┘
```

Main flows:

1. **Open from Settings › Account.** Flash `MenuAccountSettings.doAction` forwards `customAction "mcvs_offline"` to
   `PlayerProfileComponent.ProcessAccountMenuButtonAction` (game thread). The patched prefix calls
   `AdminHooks.OpenPage("account", this.World)`. The host reads the game's own anonymous credential (§6.6), mints a
   one-time ticket through `IAdminPortal.MintPageUrl`, starts the watchdog lease and calls
   `World.Browser.OpenUrl("http://127.0.0.1:51780/_mcvs/ui/#t=<ticket>", true)` (or the JNI fallback).
2. **Open from the inbox.** The CRM document carries a `newsfeeds` item whose `redirect` is the fixed marker
   `http://127.0.0.1:51780/_mcvs/ui/#news`. Its button calls `Native.OpenUrl` → `UIComponent.OpenUrl` (or
   `MenuComponent.NativeOpenUrl`) → `BrowserComponent.OpenUrl(url, …)`, whose `adminlink` prefix calls
   `AdminHooks.OpenIfPageMarker(url, this)`: for the marker it opens the page exactly as flow 1 (`trigger news`) and
   skips the original body. Without the patch the marker URL itself loads: a view-only page (§2.4).
3. **Session.** The page reads `#t=` once, removes the fragment (`history.replaceState`), asks `GET status` with the
   cookie it may already have, and only without a session `POST /_mcvs/api/v1/session {"ticket": ...}` → `Set-Cookie:
   mcvs_s=...` (§2.5).
4. **Write.** XHR with `Content-Type: application/json`, `X-MCVS: 1`, same-origin `Origin`, cookie.
5. **Share.** The server builds the file under `<data>/admin/`, calls `IAdminHost.ShareFile`; the host copies it into
   MediaStore Downloads (`Download/MCVS-Offline/`) and starts an `ACTION_SEND` chooser over the in-game browser.
6. **Import/restore/relink/undo.** Validated and staged in `<data>/admin/staged/`; applied by `admin-saves`' `Register`
   at the next start, before `McvsServer.Start` opens the port; then on probation until the TS saves the target.
7. **Save now / restart.** `ExitSequence` (server) drives host primitives: close the browser, wait for the game loop,
   request the game's own save, wait for it, then reopen the page (save now) or exit through the game's restart path.

## 2. User-facing scope and screens

### 2.1 Frame, geometry and global behaviour

* One page, `GET /_mcvs/ui/` (also `/_mcvs/ui`, `/_mcvs/ui/index.html`), compiled into the server from
  `server/Content/admin/index.html` (base64 in `BuiltInContent`, read directly from `BuiltInContent.All`, never from
  the `<data>/content/` override folder). No external assets, no fonts, no images except inline SVG/`data:` URIs.
* **Geometry.** The browser's landscape layout (`res/layout-land/activity_in_game_browser.xml`) gives the WebView 80 %
  of the screen height between two native bars of 10 % each (top: Close; bottom: Back, Forward, Refresh); the theme is
  fullscreen and the soft input mode `adjustPan` (`AndroidManifest.xml:158-163`). The WebView is therefore about **327
  CSS px** high on the S22 (~411 dp in landscape), **286 px** on 360-dp phones and **254 px** on old 320-dp phones; 640 to
  960+ px wide. The page is designed for **250–330 px of height**.
* **Layout** (`html, body {height: 100%}`, flexbox only):
  * no page top bar (the native bar already has Close);
  * a left navigation column `#nav` (112 px): `#nav-title` "MCVS Offline" (13 px) with the connection dot `#conn`, six
    buttons (`#nav-status`, `#nav-settings`, `#nav-saves`, `#nav-logs`, `#nav-actions`, `#nav-about`; labels ellipsized),
    and `#btn-close` (`<a href="exit:">`) at the bottom;
  * a content column: banners `#banners` (each banner one line, tap to expand), the scrolling pane `#pane`
    (`flex: 1; overflow-y: auto`), and a **non-scrolling action bar** `#actionbar` (40 px) that holds the current
    screen's primary buttons (Save/Discard, wizard Back/Next, Build/Share). Nothing the player must reach sits at the
    end of a long list.
* **Type and touch.** Base font 15 px; the system font scale applies (the browser never calls `setTextZoom`), so rows use
  `min-height` and padding, never fixed heights; long values wrap. Touch targets at least 36 px high (40 where space
  allows); `touch-action: manipulation` on every control (no double-tap zoom on rapid stepper taps; the WebView has
  `setSupportZoom(true)`). Dark theme. No hover-only affordances.
* **Inputs.** Prefer switches, steppers (− / value / +) and selects; typed fields only for the country code, the season
  anchor, the redeem code, log search, the paste box and notes. With `adjustPan` the keyboard covers about half of the
  pane, so a typed field carries its own small "Done" button next to it, and the pane scrolls the focused field to its
  top (`pane.scrollTop = field.offsetTop - 8`).
* **Dialogs** are in-page (`#dialog` overlay; `#dialog-body` scrolls inside, max-height = pane height − 48 px; the
  button row `#dialog-ok`, `#dialog-cancel` is always visible); never `alert/confirm/prompt` (the plain `WebChromeClient`
  makes them unreliable). Transient messages in `#toast`.
* **Banners** (stacked, each dismissable except the first three): `#banner-nosession` (no or expired session: "Open this
  page from the game: Settings › Account › MCVS Offline, or the MCVS Offline message in the inbox."; when the fragment's
  ticket was refused (§2.2 step 4), `#banner-nosession-text` says instead "This tab is from an earlier start of the game:
  close it and open the page from the game again." (WR2 F8)), `#banner-viewmode`
  (§2.4), `#banner-error` (server unreachable after 3 failed pings: "The game is not answering. Go back to the game."),
  `#banner-staged` (an operation waits for a restart, with "Restart now" and "Cancel"), `#banner-restart` (N settings
  apply after a restart, with "Restart now"), `#banner-probation` (an applied operation waits for the game's first
  save; or it was reverted automatically, with the reason).
* **Navigation** never reloads the document (every load shows the browser's loading dialog) and never changes
  `location`. **Back guard:** while there are unsaved settings, an open dialog or a running wizard, the page pushes one
  same-document history entry (`history.pushState({mcvs: 1}, '')`, feature-tested); on `popstate` it closes the dialog
  or asks "Discard your changes?" and pushes the entry again if the player stays. As soon as nothing is left to lose
  (a dialog closed, a Save, a Discard) the page takes its entry off again with `history.back()` and ignores the
  `popstate` that causes (a `popstate` onto its own entry, the browser's Forward button, is taken off the same way);
  every event handler and every API answer ends with this check. With nothing to lose no entry exists, so hardware Back
  closes the browser as players expect (`InGameBrowser.onBackPressed`: `goBack()` while the WebView can, else close).
* **Liveness.** The page pings `GET /_mcvs/api/v1/ping` every 10 s while visible and at once on `visibilitychange` to
  visible (renews the host's watchdog lease after a share target, §8.5; detects a dead server).

### 2.2 Screens

| Screen (nav id / container id) | Content | Actions |
|---|---|---|
| **Status** (`#nav-status` / `#screen-status`) | Version and build; server (started, uptime, port, bind, LAN mode, data dir, data pack); player (alias, short account id, level, revision, **last save by the game** "10:42, 7 min ago", how the player was identified); host (platform, Android API level, WebView user agent from `navigator.userAgent`, watchdog, log mirror, in-game browser state, capabilities, missing JNI members); settings file state (OK / broken); the CRM document and the Cosmetic crate's phase (dormant / guard / active, with the reason); pending staged operation, probation, the last result and the last save/restart attempt; **recent problems** (last 20 WARN/ERROR lines of the server and host loggers since boot, newest first, tap to expand); **storage usage** (profiles, backups, blobs, messages, logs, admin files, other; loaded on demand with `?storage=1`). | Refresh `#status-refresh`; Storage usage `#status-storage`. |
| **Settings** (`#nav-settings` / `#screen-settings`) | Group tabs `#grp-gameplay`, `#grp-economy`, `#grp-unlocks`, `#grp-liveops`, `#grp-diagnostics`, `#grp-advanced`. Each key: label, help text, control by type (switch, stepper, select, text, token checkboxes) with id `set-<key with '.' replaced by '-'>` (e.g. `#set-economy-versusCoinsMultiplier`), an "applies" badge (**Now** / **After the next match** / **After a restart**), a dot when it differs from the default, "Reset" link. Economy has presets (`#preset-standard`, `#preset-relaxed`, `#preset-fast`). Unlocks & codes lists the redeem codes with descriptions and a Copy button each. Advanced is a read-only table (secrets masked) with "edit settings.json on a PC to change these". `menu` keys say "after the next match (the game picks it up after you close this page)". | Action bar: Save (`#settings-save`, sends only changed keys) / Discard (`#settings-discard`); confirmations for keys with `confirm` text (§3). With a broken `settings.json` the screen is replaced by the repair panel: parse error, path, `#settings-showfile` (read-only text of the file, `superPassword` masked), `#settings-repair` ("Move the broken file aside and start from the defaults"). |
| **Saves** (`#nav-saves` / `#screen-saves`) | **Current player** card `#saves-current` (alias, id, level, revision, "last saved by the game 10:42"; backups and exports contain the progress **as of that save**). **Backups** of the selected account `#saves-backups` (date in local time, kind: Automatic / Before an event / Before import, restore or a setting / Yours, revision, level, size). **Other accounts on this phone** `#saves-accounts`. **Export** wizard. **Import** wizard. Pending staged operation, probation, last result. | Save the game now `#saves-save-now` (closes the page, the game saves, the page reopens here; §4.5); Back up now `#saves-backup-now`; Restore `restore-<id>`; Delete (own backups only) `delete-<id>`; Play this account on this phone `relink-<account>`; **Export**: Share as a file `#export-share`, Save to Downloads `#export-downloads` (only when the host can), Copy as text `#export-copy` with part size `#export-partsize` (Whole parts of 60,000 / Telegram 3,900 / Discord 1,900 characters) → "Copy part k of n" `#export-copy-next`, the current part shown read-only in `#export-text`; **Import**: paste box `#import-text` + Add `#import-add` (each paste adds the parts it contains; `#import-parts` shows "parts 1, 2 of 3 received"), or a file from `McvsOffline/import/` (`#import-files`) → summary `#import-summary` (level, revision, `hasGameProfile`, plan, warnings) → confirmation checkbox `#import-confirm` ("Replace the progress of <alias> with this save" / "Play this save on this phone; your current save stays on the phone") → Stage `#import-stage`; Cancel staged `#staged-cancel`; Undo the last operation `#staged-undo`; Restart now `#staged-restart`. |
| **Logs** (`#nav-logs` / `#screen-logs`) | File list (`#logs-files`: current, rotated, crash, exception, thread dump, exit, unknown endpoints; size and date); viewer (`#logs-view`, monospace, wrapped) showing the tail as parsed entries. | Level filter `#logs-level` (All / Info+ / Warnings+ / Errors), search `#logs-search`, lines `#logs-lines` (200/500/2000), auto-refresh 5 s `#logs-autorefresh`, Copy visible text `#logs-copy` (3 fallbacks, §7.4). **Share**: scope `#logs-scope` (Recent (default) / Everything), note `#logs-note`, Build `#logs-build` → "mcvs-logs-….zip, 2.3 MB: fits Discord (10 MB) and Gmail (25 MB)" → Share `#logs-share` / Save to Downloads `#logs-downloads`; Share a text report `#logs-share-text`. After starting a share: "Come back to the game within a minute." |
| **Actions** (`#nav-actions` / `#screen-actions`) | "Where are my files" `#action-files`: data dir, logs dir, `Download/MCVS-Offline/`, the adb commands of PLAYER-SETUP §6/§8, the import folder and which Android versions can write it (§6.8). | Restart the game (`#action-restart`; the confirmation shows the last save time and a checkbox "Restart even if the game does not save (you may lose the progress since 10:42)"; only when the host can); Rebuild the CRM document (`#action-crm-reload`, "takes effect when you close this page"); Grant unlock-all now (`#action-unlock-now`, only when `unlockAll` is on: "starts when you close this page; close it now?"). |
| **About** (`#nav-about` / `#screen-about`) | Project name, version `#about-version`, one paragraph on what the page is ("runs inside your game; nothing leaves the phone unless you share it"), the WebView user agent `#about-ua`. | Close (`exit:`). |

### 2.3 Page technology rules (checked by WP4's tests, §11 item 4)

* One `index.html`, ≤ 200 KB, one inline `<style>`, one inline `<script>`; `<meta charset="utf-8">`,
  `<title>MCVS Offline</title>`; no `src=`/`href=` to anything but `exit:` and `#`.
* **ES5 only**: no `let/const/class/=>/template literals/.../for-of/async/await/default parameters/shorthand
  methods`; no `Promise` construction, `fetch`, `Map/Set/Symbol`, `Object.assign/entries/values`, `Array.from`,
  `String.prototype.includes/startsWith/endsWith/repeat/padStart`, `Array.prototype.find/findIndex/includes/fill`,
  `URLSearchParams`; no `localStorage/sessionStorage/indexedDB`, no `alert/confirm/prompt`, no `eval/new Function`,
  no `innerHTML` except `el.innerHTML = ''`, no `document.write`. `navigator.clipboard.writeText(...).then(...)` is
  allowed only inside a feature test.
* CSS: no custom properties (`var(--…)`), no grid, no `position: sticky`, no flex `gap`, no `vh` units, no fixed `height`
  on text rows (`min-height` only); flexbox is fine.
* Network: XHR with relative URLs `/_mcvs/api/v1/...` only; `withCredentials` not needed (same origin); every
  non-GET request sets `Content-Type: application/json` and `X-MCVS: 1` and sends a JSON object (at least `{}`).
* DOM surface the page may use (the node smoke harness of §11 item 4 implements exactly this): `document.getElementById`,
  `createElement`, `createTextNode`, `appendChild`, `removeChild`, `insertBefore`, `firstChild`, `textContent`,
  `className`, `setAttribute/getAttribute`, `style.*`, `addEventListener('click'|'change'|'input')`,
  `document.addEventListener('visibilitychange')`, `document.hidden`, `window.addEventListener('popstate')`,
  `value/checked/disabled`, `select()`, `focus()`, `scrollTop`, `offsetTop`,
  `setTimeout/clearTimeout/setInterval/clearInterval`, `XMLHttpRequest`, `location.hash/pathname`,
  `history.replaceState`/`history.pushState`/`history.back` (feature-tested), `navigator.userAgent`, `navigator.clipboard`
  (feature-tested), `document.execCommand('copy')` (feature-tested).
* Dynamic text is always set with `textContent`; numbers and dates are formatted in JS (dates from ISO 8601 UTC to the
  phone's local time with `new Date(iso)` and manual padding; no `Intl`). Sizes are shown in KB/MB with one decimal.

### 2.4 View mode (no session possible)

When the page loads with `#news` and no ticket (a build without `adminlink`, or the hook did not run), or with no
fragment at all (Chrome, a bookmark), it shows `#banner-viewmode`: "This view cannot change anything. Open MCVS Offline
from the game: Settings › Account › MCVS Offline (or the inbox message in a build with the default patches)." and the
About screen; every other screen is disabled. `GET ping` (no data) still runs, so the device test can prove that the
browser loads our page, cleartext to 127.0.0.1 works and the script runs, without any patch (C2).

### 2.5 Page boot and the ticket (activity re-creation)

The browser activity lacks `uiMode|fontScale|locale|screenLayout` in its `configChanges` (manifest :158-163), so a dark
mode switch or a font-size change re-runs `onCreate`, which loads `extra_url` again with the already used `#t=` (or
re-POSTs it in post mode). The boot sequence tolerates that:

1. Parse `location.hash` once: `t=` followed by **exactly 32 lower-case hex characters** (stop at `&` or the end),
   `s=<screen>` (one of the six screen names), `news`, `r=nosave`. Keep the values in JS memory.
2. Remove the fragment at once: `history.replaceState(null, '', location.pathname)` (feature-tested; else
   `location.hash = ''`).
3. `GET status` with whatever cookie the WebView has. 200 → the session is valid: ignore the ticket.
4. Otherwise, with a ticket: `POST session`; 200 → `GET status`; 401 → `#banner-nosession` with the stale-tab text in
   `#banner-nosession-text` ("This tab is from an earlier start of the game: close it and open the page from the game
   again.": tickets live in the game's memory and are used once, so a refused ticket means a tab kept or re-opened from
   an earlier start, not a link the game just opened; WR2 F8); any other status → `#banner-nosession` with its usual
   text. Without a ticket: `#news` → view mode (§2.4), else `#banner-nosession`.
5. Select the screen from `s=` (default Status); `r=nosave` shows the toast "The game did not save."

Unsaved settings and wizard state are lost when Android re-creates the browser; the Settings screen says so in its help
line ("Changing the system theme or font size while this page is open discards unsaved changes"). Imports survive it:
pasted parts are kept on the server (§4.6).

## 3. Settings catalogue (the allowlist)

The schema lives in `server/Services/Admin/SettingsSchema.cs` (WP2) and is served to the page; the page has no key
list of its own. `apply`:

* `live`: the server reads the value on every use (`Set` is enough).
* `menu`: the value goes into the CRM document (its cache key is the whole settings snapshot) or live-ops snapshot;
  the game applies it at the **next CRM fetch**, which the menu server forces on every return to the main menu (after a
  match or the firing range), or at the next start. The game loop is paused while the page is open, so nothing happens
  before the page closes. Page wording: "After the next match".
* `restart`: read once at boot (host copy, args.txt, log sinks, handshake). Page wording: "After a restart".

Types: `bool`; `int` (JSON integer, stored as `long`); `number` (JSON number, stored as `double`, NaN/∞ refused);
`enum` (string from `options`); `string` (`pattern`, `maxLength`); `tokens` (space-separated subset of `options`,
no duplicates, stored as one string). Page ranges are tighter than what the modules clamp to; the server enforces the
page range. Schema fields per key: `key`, `label`, `help`, `type`, `min`, `max`, `step`, `options`, `pattern`,
`maxLength`, `default`, `apply`, `editable`, `confirm` (text shown before saving a change), `confirmWhen` (`any`,
`enable` = false → true, `decrease`), `backupBefore` (the server takes a `presettings` backup of the current player's
save before saving the change, §4.4).

### 3.1 Editable keys

| Group | Key | Label | Type / page range | Default | Apply | Help / confirm |
|---|---|---|---|---|---|---|
| Gameplay | `instantBots` | Instant bot matches | bool | true | menu | Matches start at once and bots fill the empty slots. Off: the game waits for other players first. |
| Gameplay | `crm.practiceTile` | Practice tile in the main menu | bool | false | menu | Shows the game's own hidden PRACTICE tile (a bot match) as the last tile of the main menu. With the bottile patch (default) it starts the same bot-filled, full-reward match as Battle > Classic multiplayer, without the mode and group screens; without it, the game's free bot match (5 Credits per win). Untested: where the tile sits in this menu and how it looks; turn it off again if the menu looks wrong. Two CRM SET cells on MCVS_Meta `MainMenuElements` row 13 ([protocol/content.md](../protocol/content.md) step 5). |
| Gameplay | `admin.settingsMenu.trim` | Shorter Settings menu | bool | true | live | Hides SOCIAL MEDIA, FORUM and CUSTOMER CARE (and MORE GAMES on phones) in the game's Settings menu, which cannot work offline, and centres the tiles that are left, with ABOUT alone below them. Applies the next time Settings opens. Read by the embedded host on every Settings opening (`SettingsMenu/UiNatives`, [settings-menu.md](settings-menu.md) §3.2). Both platforms. |
| Gameplay | `admin.settingsMenu.performance` | PERFORMANCE in the Settings menu | bool | true | live | Adds a PERFORMANCE tile to the game's Settings menu, as on Steam: GRAPHICS, ANTIALIASING, SCREEN SCALE, FRAME RATE LIMIT and LOW RESOLUTION MAPS. GRAPHICS SETTINGS and FRAME RATE LIMIT then leave Gameplay Settings, also during a match. FRAME RATE LIMIT offers 90 and 120 from the next start. Off: the game's own menus. Applies the next time Settings opens. Android only (not shown on PC; settings-menu.md §3.3-§3.5). |
| Gameplay | `devMenu` | Developer cheat menu | bool | false | restart | Gameloft's developer menu (`IsDevelopment` in the menu server handshake). `confirmWhen: enable`, `backupBefore: enable`: "Cheats can damage your profile. A backup of your save is made now." |
| Gameplay | `leaderboards.virtualPlayers` | Leaderboard opponents | bool | true | live | Division leaderboards are filled with generated players. |
| Gameplay | `geo.country` | Country | string `^[A-Z]{2}$` | `US` | restart | Two-letter country code reported to the game. |
| Economy | `economy.versusCoinsMultiplier` | VersusCoins per match | number 0.1–20, step 0.1 | 1.0 | menu | Multiplies VersusCoins for wins, defeats, MVP and the daily cap. |
| Economy | `economy.creditsMultiplier` | Credits per match | number 0.1–20, step 0.1 | 1.0 | menu | Same for Credits (faster loot crates). |
| Economy | `economy.xpMultiplier` | XP speed | number 0.1–20, step 0.1 | 1.0 | menu | Divides the XP needed per player level and agent mastery level. |
| Economy | `economy.priceMultiplier` | Shop prices | number 0.05–10, step 0.05 | 1.0 | menu | Scales every VersusCoins price in the offline shop (at least 1). |
| Economy | `crm.dailyCapMultiplier` | Daily cap | number 0.1–20, step 0.1 | 1.0 | menu | Extra factor on the two daily caps only. |
| Economy | `economy.collector.enabled` | Cosmetic crate | bool | true | menu | Random cosmetic crates in daily missions, weekly events and season rewards. Off restores the original rewards. Switching restarts the running daily missions and weekly event. `confirmWhen: any` (no backup): "The running daily missions and weekly event are replaced after the next match: their progress is lost (rewards already claimed stay)." Shown as `CollectorOptions` reads it (§4.4 `value`). |
| Economy | `economy.collector.itemsPerCrate` | Items per Cosmetic crate | int 1–10 | 5 | menu | Shown as `CollectorOptions` reads it. |
| Unlocks & codes | `unlockAll` | Unlock everything | bool | false | live | Grants what UNLOCKALL grants, through the inventory, at the game's next save (~11,400 rewards; the level jumps to ~46 and the game lags while it levels up). `confirmWhen: enable`, `backupBefore: enable`: "This cannot be undone: turning it off later removes nothing. A backup of your save is made now." |
| Unlocks & codes | `assets.couponsEnabled` | Unlock codes | bool | true | live | Settings › Account › Redeem code accepts the codes listed below. |
| Unlocks & codes | `crm.redeemCoupons` | Redeem-code button | bool | true | menu | Shows the Redeem code button in Settings › Account. |
| Unlocks & codes | `admin.accountMenu.hideDeadButtons` | Only Redeem code and MCVS Offline in Settings > Account | bool | true | live | Hides the Facebook, Google Play Games and Restore buttons, which cannot work offline, and centres the buttons that are left. Applies the next time Settings > Account opens. Read by the embedded host (`AdminHost.HideDeadAccountButtons`) in `AdminHooks.AccountMenuButtons`, §9.2. Not shown on PC (no effect there, docs/design/pc-port.md §3.6). |
| Unlocks & codes | `unlocks.batchSize` | Rewards per batch | int 50–400, step 50 | 400 | live | Unlock-all rewards per batch; keep at most 400 (reward popups). |
| Unlocks & codes | `admin.redeemCode` | Code that opens this page | string `^([A-Z0-9]{4,20})?$` | `MCVSOFFLINE` | live | Typing it in Redeem code opens this page (the game then says the code does not exist). Empty: off. |
| Live ops | `liveops.enabled` | Live events | bool | true | menu | Division seasons, daily missions and weekly events. |
| Live ops | `liveops.divisionSeasons` | Division seasons | bool | true | menu | |
| Live ops | `liveops.dailyQuests` | Daily missions | bool | true | menu | |
| Live ops | `liveops.weeklyEvents` | Weekly events | bool | true | menu | |
| Live ops | `liveops.seasonDays` | Season length (days) | int 1–28 | 7 | menu | `confirmWhen: any`, `backupBefore: any`: "Changing this starts a new season now; the running season's ranks and progress reset. A backup of your save is made now (season ranks are not part of it)." |
| Live ops | `liveops.seasonStart` | Season anchor (UTC) | string ISO 8601 (`^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$`) | `2024-01-01T00:00:00Z` | menu | Same confirm and backup as season length. |
| Live ops | `liveops.dailyResetHour` | Daily reset hour (UTC) | int 0–23 | 0 | menu | |
| Live ops | `liveops.modifiers` | Weekly modifier | enum `rotate`, `off`, `standard`, `coinRush`, `trophyFever`, `creditRush`, `mvpBounty`, `rivalSurge` | `rotate` | menu | `rotate` changes it every season ([LIVEOPS.md](../LIVEOPS.md) §8). |
| Live ops | `liveops.difficulty` | Event difficulty | number 0.1–10, step 0.1 | 1.0 | menu | |
| Live ops | `liveops.seasonRewardMultiplier` | Season rewards | number 0.1–20, step 0.1 | 1.0 | menu | |
| Live ops | `liveops.arenaEvents` | Special Events | bool | true | menu | Battle › Special Event ([LIVEOPS.md](../LIVEOPS.md) §8a). Off applies after a restart once the game has shown one (§8a.3). |
| Live ops | `liveops.arenaRotation` | Special Event mode | enum `rotate`, `off` and the 23 mode ids of `liveops.json` `arena.modes` (`SettingsSchema.ArenaRotationChoices`, kept equal by a test) | `rotate` | menu | A mode id pins it. |
| Live ops | `liveops.arenaHours` | Special Event length (hours) | int 1–168 | 24 | menu | Windows run back to back from the daily reset hour; a divisor of 24 keeps them aligned to the reset. |
| Live ops | `liveops.arenaMap` | Special Event map | enum `Random`, `Snow`, `Desert`, `Favelas`, `Rooftop`, `DogDays`, `Port`, `Stadium`, `Stadium2` | `Random` | menu | |
| Live ops | `crm.newsfeedLink` | Inbox message for this page | bool | true | menu | The "MCVS Offline" inbox message (§9.1). Off removes it at the next CRM fetch. |
| Diagnostics | `storage.backups.keep` | Automatic backups kept | int 0–200 | 20 | live | Per account. Counts only the automatic backups taken every N minutes; backups before imports, restores, risky settings and events, and your own backups are kept separately (§6.3). 0: no new automatic backups (existing ones stay). `confirmWhen: decrease`: "Older automatic backups beyond the new number are deleted at the next automatic backup." |
| Diagnostics | `storage.backups.intervalMinutes` | Minutes between automatic backups | int 5–1440 | 30 | live | |
| Diagnostics | `storage.autoRestoreCorrupt` | Repair unreadable saves | bool | true | live | An unreadable save file is replaced by its newest readable backup (the broken file is kept). |
| Diagnostics | `logging.fileLevel` | server.log detail | enum `trace`, `debug`, `info`, `warn`, `error` | `debug` | restart | |
| Diagnostics | `logging.keepStarts` | Starts whose logs are kept | int 0–10 | 1 | restart | Host key (Android and PC; the standalone server ignores it). "At every start the game deletes the logs of older starts: in logs/ (server, bootstrap and game logs with their numbered files, crash, exception, thread dump and exit files) and their copies in Downloads/MCVS-Offline. 1 keeps the previous start next to the current one; 0 keeps only the current start. The numbered log files also stay within their own file count. Saves, settings and the zips you saved are never deleted." PC wording: "in McvsOffline\logs and %TEMP%\McvsOffline\logs (server, bootstrap and game logs with their numbered files, crash and exception files)", no Downloads copies (§7.7) |
| Diagnostics | `gameLog.levels` | Game log levels | tokens of `error`, `warning`, `info`, `debug` | `error warning` | restart | A build patched with `--game-log all` logs every level anyway. |
| Diagnostics | `gameLog.mute` | Muted game log categories | string `^([A-Za-z]+:(all|debug|info|warning|error))?( [A-Za-z]+:(all|debug|info|warning|error))*$`, ≤ 512 | `profile:debug` | restart | |
| Diagnostics | `gameLog.file` | Copy the game's log to logs/game.log | bool | true | restart | |
| Diagnostics | `diagnostics.watchdog` | Freeze watchdog | bool | true | restart | |
| Diagnostics | `diagnostics.threadDump` | Thread dumps on freezes | bool | true | restart | |
| Diagnostics | `diagnostics.publicLogCopy` | Log copies in Downloads/MCVS-Offline | bool | true | restart | Android 11 and later. |
| Diagnostics | `tracking.disable` | Block the game's analytics uploads | bool | false | live | |
| Graphics | `graphics.msaa` | Antialiasing | enum `auto`, `off`, `2x`, `4x`, `8x` | `auto` | restart | ANTIALIASING on the game's PERFORMANCE page: GameOptions `renderTargetMSAA` in the `graphics` rewrite; `auto` leaves the text byte-identical. Android only ([settings-menu.md](settings-menu.md) §2). |
| Graphics | `graphics.renderScale` | Screen scale | string `^(auto\|[0-9]{1,3})$`, at most 100 | `auto` | live | SCREEN SCALE on the PERFORMANCE page: `RenderTargetSizeScale` = `v > 0.97 ? 1 : 0.4 + 0.6 v`, at once and after every start. Android only. |
| Graphics | `graphics.lowResMaps` | Low resolution maps | enum `auto`, `on`, `off` | `auto` | menu | LOW RESOLUTION MAPS on the PERFORMANCE page: the levels' low-end layers from the next level load. Android only. |

New keys introduced by this design (defaults live in code, nothing is added to `settings.json` automatically):
`admin.redeemCode`, `crm.newsfeedLink` (editable above), and the file-only keys `admin.enabled` (bool, true; kill
switch), `admin.openVia` (enum `auto`, `game`, `jni`, `post`; §8.3), `admin.restartVia` (enum `game`, `host`, `none`;
default `game`; §8.8), `security.hostCheck` (bool, true; the Host allowlist on every route, §5.2).

### 3.2 Advanced (read-only on the page)

Shown with their effective value: `port`, `bind`, `publicUrl`, `lanMode`, `superCredential`, `superPassword` (shown as
`•••• (default)` or `•••• (custom)`), `dataCenter`, `logging.consoleLevel`, `logging.bodyLimit`,
`logging.fileMaxBytes`, `logging.fileCount`, `embedded.manageArgs`, `embedded.lanAdvertiseHost`,
`embedded.nativeOnlineSdk`, `embedded.physics.serverMultithreaded`, `embedded.physics.clientMultithreaded`,
`embedded.physics.stepGate`, `embedded.physics.stepGateTimeoutMs`, `install.manageInstaller`,
`install.suppressUpdateCheck`, `install.rebuildIndex`, `install.verifyRestoredData`, `install.monitorInstaller`,
`crm.usePreconfiguration`, `crm.offlineStore`, `crm.iap`, `crm.omitOwnedSignatures`, `crm.overlay` (shown as "not set"
or "set, N characters"), `crm.pointcutActions` (count), `assets.coupons` (count), `matchmaker.tsRoomCapacity`,
`matchmaker.gsRoomCapacity`, `storage.cache`, `storage.absentField`, `auth.requireSuperPassword`, `admin.enabled`,
`admin.openVia`, `admin.restartVia`, `security.hostCheck`, `graphics.fpsListVersion` ("Frame rate limit list": which
FRAME RATE LIMIT list the stored index belongs to, written by the embedded host only; Android, [settings-menu.md](settings-menu.md) §2).

**Server-side denylist** (refused with 400 `not_editable` even if a future schema edit marked them editable):
`port`, `bind`, `publicUrl`, `lanMode`, `superCredential`, `superPassword`, `dataCenter`, `pandora.*`,
`embedded.manageArgs`, `embedded.lanAdvertiseHost`, `crm.overlay`, `crm.pointcutActions`, `crm.developerIpRange`,
`assets.coupons`, `admin.enabled`, `admin.openVia`, `admin.restartVia`, `security.*`, `install.*`. Any key not in §3.1 is
refused the same way.

### 3.3 Presets (Economy)

| Id | Label | Values |
|---|---|---|
| `standard` | Standard | the five economy keys = 1.0 |
| `relaxed` | Relaxed | `economy.versusCoinsMultiplier` 2, `economy.priceMultiplier` 0.5, others 1.0 |
| `fast` | Fast | `economy.versusCoinsMultiplier` 5, others 1.0 |

A preset only fills the controls; Save writes them. It touches only these five keys (a key one preset leaves out goes
back to its default); the Cosmetic crate's `economy.collector.*` keys in the same group keep their value.

## 4. HTTP API

### 4.1 Conventions

* Prefix `/_mcvs/api/v1/`. JSON bodies UTF-8, `Content-Type: application/json; charset=utf-8`. Times are ISO 8601
  UTC with milliseconds (`TimeFormat.Iso8601Millis`). Sizes are bytes (integers).
* Every API response carries `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`. No CORS header is ever sent.
* Errors: HTTP status + `{"error": "<code>", "message": "<sentence for the player>", "details": {...}?}`.

| Status | Codes |
|---|---|
| 400 | `bad_request` (malformed JSON, missing field), `invalid` (validation; `details.problems: [{key, message}]`), `not_editable`, `not_deletable` |
| 401 | `no_session`, `session_expired`, `ticket_invalid` (unknown, used or expired; never says which) |
| 403 | `csrf` (missing `X-MCVS`, bad `Origin`, cross-site `Sec-Fetch-Site`, non-JSON write; the single exemption is §5.2 rule 4). The "never 403" rule of PROTOCOL.md is about Federation routes the game calls; the game never calls `/_mcvs/*`. |
| 404 | `not_found` (also every gate refusal, so the routes stay invisible) |
| 405 | `method_not_allowed` (incl. every `OPTIONS` under `/_mcvs/`) |
| 409 | `settings_fallback`, `file_changed`, `staged_exists`, `current_player_unknown`, `newer_save`, `busy`, `restarting`, `manual_full`, `nothing_to_undo`, `no_game_profile`. While a save-now or restart sequence runs, every saves route that takes an account lock answers `busy` (saving) or `restarting` |
| 413 | `too_large` (`details.limit`; an export adds `details.unit` `"parts"` (more than 999 parts of `details.partChars`) or `"bytes"` (more than the transfer text can carry)) |
| 415 | `unsupported_media_type` |
| 429 | `rate_limited` (`details.retryAfterSeconds`) |
| 500 | `internal` (logged with the route name, never with the body), `save_failed` |
| 501 | `not_supported` (the host cannot do it: share, restart, save, clipboard) |
| 503 | `admin_disabled` (`admin.enabled` is false) |

### 4.2 Route table

`S` = session required. "Limit" is the body limit, enforced **at header time** (§5.2 rule 5) and again by `AdminApi`.
Package = who implements the route.

| Method | Path | Route name | S | Limit | Uses | Package |
|---|---|---|---|---|---|---|
| GET | `/_mcvs/ui`, `/_mcvs/ui/`, `/_mcvs/ui/index.html` | `admin.ui` | – | – | `BuiltInContent.All["admin/index.html"]` | WP2 |
| POST | `/_mcvs/ui` (form `t=<ticket>[&s=<screen>][&r=nosave]`, only with `admin.openVia=post`) | `admin.ui.post` | ticket | 1 KiB | `AdminSecurity` | WP2 |
| POST | `session` | `admin.session.open` | ticket | 1 KiB | `AdminSecurity` | WP2 |
| DELETE | `session` | `admin.session.close` | S | 1 KiB | | WP2 |
| GET | `ping` | `admin.ping` | optional | – | `IAdminHost.PageAlive` | WP2 |
| GET | `status[?storage=1]` | `admin.status` | S | – | `ServerContext`, `McvsVersion`, `CurrentPlayer`, `GameSaveTracker`, `IProfileAccess`, `ServerContext.Problems`, `IAdminHost.Describe/RecentProblems`, `CrmDocumentCache` | WP2 |
| GET | `settings` | `admin.settings.get` | S | – | `Settings` (server copy), `SettingsSchema` | WP2 |
| POST | `settings` | `admin.settings.set` | S | 64 KiB | `Settings.Set/Save`, `AdminApi.CreateBackup` | WP2 |
| POST | `settings/reset` | `admin.settings.reset` | S | 16 KiB | | WP2 |
| GET | `settings/file` | `admin.settings.file` | S | – | only while `Settings.IsFallback`; `superPassword` masked | WP2 |
| POST | `settings/repair` | `admin.settings.repair` | S | 1 KiB | `Settings.MoveAsideAndReset` | WP2 |
| GET | `codes` | `admin.codes` | S | – | `CouponCatalog.Describe()` | WP2 |
| POST | `clipboard` | `admin.clipboard` | S | 512 KiB | `IAdminHost.CopyToClipboard` | WP2 |
| POST | `actions/save-now` | `admin.actions.saveNow` | S | 1 KiB | `ExitSequence` | WP2 |
| POST | `actions/restart` | `admin.actions.restart` | S | 1 KiB | `ExitSequence` | WP2 |
| POST | `actions/crm-reload` | `admin.actions.crmReload` | S | 1 KiB | `CrmDocumentCache.Invalidate` | WP2 |
| POST | `actions/unlock-now` | `admin.actions.unlockNow` | S | 1 KiB | `UnlockGranter.Enqueue` | WP2 |
| GET | `saves` | `admin.saves.list` | S | – | `AccountStore`, `LocalProfileStore`, `CurrentPlayer`, `GameSaveTracker` | WP3 |
| GET | `saves/{account}/backups` | `admin.saves.backups` | S | – | `ProfileBackups.List` | WP3 |
| POST | `saves/{account}/backups` | `admin.saves.backup` | S | 1 KiB | `LocalProfileStore.CreateBackup(…, "manual")` | WP3 |
| DELETE | `saves/{account}/backups/{id}` | `admin.saves.backupDelete` | S | 1 KiB | `LocalProfileStore.DeleteBackup` (manual only) | WP3 |
| POST | `saves/{account}/restore` | `admin.saves.restore` | S | 1 KiB | `StagedOperations` | WP3 |
| POST | `saves/{account}/relink` | `admin.saves.relink` | S | 1 KiB | `StagedOperations` | WP3 |
| POST | `saves/{account}/export` | `admin.saves.export` | S | 1 KiB | `SaveBundle`, `SaveTransfer`, `IAdminHost.ShareFile` | WP3 |
| GET | `saves/{account}/export?format=transfer\|json` | `admin.saves.download` | S | – | attachment (PC/standalone) | WP3 |
| GET | `saves/import/files` | `admin.saves.importFiles` | S | – | `<data>/import/` | WP3 |
| POST | `saves/import/paste` | `admin.saves.paste` | S | 512 KiB | `ImportUploads`, `SaveTransfer`, `SaveBundle` | WP3 |
| POST | `saves/import/validate` | `admin.saves.validate` | S | 1 KiB | `{"file"}` from `<data>/import/` | WP3 |
| POST | `saves/import/stage` | `admin.saves.stage` | S | 1 KiB | `StagedOperations` | WP3 |
| GET | `saves/staged` | `admin.saves.staged` | S | – | | WP3 |
| DELETE | `saves/staged` | `admin.saves.unstage` | S | 1 KiB | | WP3 |
| POST | `saves/staged/undo` | `admin.saves.undo` | S | 1 KiB | `StagedOperations` | WP3 |
| GET | `logs` | `admin.logs.list` | S | – | `IAdminHost.Describe().LogsDir/FallbackLogsDir`, `<data>/logs` | WP3 |
| GET | `logs/{name}?dir=&lines=&min=&q=&raw=` | `admin.logs.tail` | S | – | `LogTail` | WP3 |
| POST | `logs/zip` | `admin.logs.zip` | S | 8 KiB | `LogsZip`, `ZipWriter` | WP3 |
| POST | `logs/share` | `admin.logs.share` | S | 1 KiB | `IAdminHost.ShareFile` | WP3 |
| GET | `logs/bundle?scope=recent\|all` | `admin.logs.download` | S | – | `LogsZip` (attachment, PC/standalone) | WP3 |
| POST | `logs/share-text` | `admin.logs.shareText` | S | 8 KiB | `IAdminHost.ShareText` | WP3 |
| any | `_mcvs/api/{*rest}` (catch-all, `Router.CatchAllOrder`) | `admin.api.unknown` | – | – | 404 `not_found` (no unknown-endpoint logging) | WP2 |

Literal routes are registered before templated ones with the same shape (`saves/import/*` and `saves/staged*` before
`saves/{account}/*`, `logs/bundle`, `logs/zip`, `logs/share*` before `logs/{name}`): all are `Router.DefaultOrder`, so
registration order decides. **Identifiers** (anything else → 404 `not_found`, never a filesystem lookup):

* `{account}`: `^[0-9a-f]{24}$` (the form of `AccountStore.DeriveAccountId`) and present in `AccountStore`, or the
  literal `current` (resolved with §6.6). Lower case only: Android's emulated storage is case-insensitive, so `0A…` and
  `0a…` would share one profile file while being two `AccountStore` entries.
* `{id}` (backup): `ProfileBackups.IsSafeId` and returned by `ProfileBackups.List(account)`.
* `uploadId`: `^u-[0-9a-f]{16}$`; transfer set: `^[0-9a-f]{8}$`.
* `{"file"}`: equal (ordinal) to a name returned by `GET saves/import/files` (regular file, `*.json` or `*.txt`,
  `^[A-Za-z0-9._ -]{1,128}$`, ≤ 16 MiB).
* `{name}` (logs): `^[A-Za-z0-9._-]{1,128}$` and listed by `GET logs` in the chosen directory (`dir` 0 = primary
  `<data>/logs`, 1 = host fallback directory).
* Names the server builds for files and `Content-Disposition` use only `[A-Za-z0-9_-]` taken from the alias when
  `AliasRegistry.IsWellFormed(alias)`, else the account id.

### 4.3 Session, ping, status

`POST session` request `{"ticket": "<32 lower-case hex>"}`. Response 200:

```json
{"ok": true, "session": {"created": "2026-10-26T10:15:00.123Z", "trigger": "account", "expiresIdle": 7200}}
```

with `Set-Cookie: mcvs_s=<64 hex>; Path=/_mcvs/; HttpOnly; SameSite=Strict` (no `Max-Age`: a session cookie; the
secret is per process anyway). The INFO log line is `admin: session opened (trigger account, <User-Agent>)`; it never
contains the ticket or cookie. `DELETE session` → 200 `{"ok": true}` with `Set-Cookie: mcvs_s=; Path=/_mcvs/; Max-Age=0`.

`GET ping` → 200 `{"ok": true, "session": true|false, "now": "...", "uptimeSeconds": 123}`. With a valid session it
calls `IAdminHost.PageAlive()` (watchdog lease, §8.5).

`GET status` → 200:

```json
{
  "version": "0.1.0-dev", "build": "9",
  "server": {"started": "...", "uptimeSeconds": 3600, "instance": "mcvs1a2b3c4d", "port": 51780, "bind": "127.0.0.1",
             "lanMode": false, "hostCheck": true, "dataDir": "/storage/emulated/0/Android/data/.../files/McvsOffline",
             "dataPack": ".../files/Data"},
  "host": {"platform": "android", "sdkInt": 36, "deviceId": "...", "logsDir": "...", "fallbackLogsDir": null,
           "publicLogsDir": "/storage/emulated/0/Download/MCVS-Offline", "mirror": "active, 3 files, last copy 12 s ago",
           "watchdog": "game thread tid 1234 ..., paused for the page", "browser": "initialized", "browserOpen": true,
           "freeBytes": 12345678901, "gameCredential": "anonymous:a1b2…f9e8", "restartVia": "game", "missingJni": [],
           "capabilities": {"openPage": true, "shareFile": true, "saveToDownloads": true, "shareText": true,
                            "clipboard": true, "toast": true, "save": true, "exit": true}},
  "settings": {"file": ".../settings.json", "fallback": false, "error": null, "restartPending": 2},
  "player": {"account": "0123456789abcdef01234567", "fedId": "fed_id:0123...", "alias": "...", "source": "game",
             "level": 37, "revision": 512, "modified": "...", "lastGameSave": "2026-10-26T10:42:01.000Z",
             "hasGameProfile": true},
  "staged": {"pending": null, "probation": null,
             "last": {"id": "20261026T101500123Z-import", "result": "applied", "at": "...", "message": "..."}},
  "exit": {"state": "idle", "last": {"kind": "restart", "result": "cancelled", "at": "...",
                                     "message": "The game did not save, so it was not restarted."}},
  "problems": [{"time": "...", "level": "WARN", "category": "crm", "message": "...", "source": "server"}],
  "crm": {"built": "...", "chars": 89282, "problems": 0, "collectorPhase": "Dormant|Guard|Active", "collectorReason": "..."|null},
  "storage": {"profiles": {"files": 1, "bytes": 612000}, "backups": {...}, "blobs": {...}, "messages": {...},
              "logs": {...}, "admin": {...}, "other": {...}, "total": 41000000, "scanCapped": false}
}
```

`host` is `null` without an `IAdminHost` (standalone). `player.lastGameSave` is the time of the last
`storage.profile.changed` event with source `http` for that account in this process (`GameSaveTracker`, §8.8), else
null; the page then shows the profile's `modified` time. `exit.state`: `idle`, `saving`, `restarting`. `storage` is
present only with `?storage=1`; the scan walks `<data>` (never `Data/`) and stops after 20,000 files or 500 ms
(`scanCapped: true`). `problems`: newest first, at most 20, from `ServerContext.Problems` (WP1) and
`IAdminHost.RecentProblems(20)`, each message cut to 2,000 characters (already redacted, §5.4). While `exit.state` is
not `idle`, `player.level` is omitted (no account lock is taken, §4.5).

### 4.4 Settings

`GET settings` → 200:

```json
{
  "file": ".../settings.json", "fallback": false, "error": null, "fingerprint": "<length>:<mtime ticks>",
  "restartPending": ["devMenu"],
  "groups": [
    {"id": "economy", "label": "Economy", "keys": [
      {"key": "economy.versusCoinsMultiplier", "label": "VersusCoins per match", "help": "...", "type": "number",
       "min": 0.1, "max": 20, "step": 0.1, "default": 1.0, "value": 2.0, "apply": "menu", "editable": true,
       "confirm": null, "confirmWhen": null, "backupBefore": null}
    ]},
    {"id": "advanced", "label": "Advanced", "keys": [
      {"key": "superPassword", "label": "Super password", "type": "string", "editable": false, "display": "•••• (default)"}
    ]}
  ],
  "presets": [{"id": "relaxed", "label": "Relaxed", "values": {"economy.versusCoinsMultiplier": 2, "economy.priceMultiplier": 0.5}}]
}
```

`value` is `Settings.Get(key)` (effective), coerced to the schema type for display (a value of the wrong type in the
file is shown with `"invalidStored": true`, `"invalidNote"` and the default). The Cosmetic crate's two keys are shown as
`CollectorOptions` reads them instead (`SettingsSchema.Read`: the shorthand `"collector": false`, an unreadable kill
switch or settings.json = off, item counts parsed and clamped), flagged with a note saying what the server uses whenever
the file holds something else; a write of either key turns a non-object `economy.collector` into
`{"enabled": <what the collector read>}` first, so the kill switch survives. `restartPending` lists the `restart` keys whose stored
value differs from the value at boot (snapshot taken in `AdminService.Register`).

`POST settings` request:

```json
{"changes": {"economy.versusCoinsMultiplier": 2.0, "unlockAll": true}, "fingerprint": "<from GET>", "force": false}
```

Processing (one lock for all settings writes):

1. 409 `settings_fallback` when `Settings.IsFallback`.
2. Every key must be in §3.1 and not in the denylist (else 400 `not_editable`), every value must validate (400 `invalid`
   with all problems); nothing is written when anything is wrong.
3. File check: the current `settings.json` length and last-write time must equal the ones recorded at boot or after
   this module's last save; otherwise 409 `file_changed` unless `force: true`. The page's conflict dialog offers, as the
   default, "Keep the edited file and restart" (discards the page's changes and runs the restart of §4.5, which loads the
   file) and, second, "Overwrite it". With `force`, the server first copies the file on disk to
   `settings.json.bak-<yyyyMMddTHHmmssZ>` (newest 3 kept) and returns its name in `overwrittenCopy`.
4. **Backups before risky changes**: if any changed key's `backupBefore` condition holds (`enable`: false → true;
   `any`: any change), the server calls `AdminApi.CreateBackup(currentAccount, "presettings")` once before saving
   (§6.3). No current player or no stored profile → no backup, a warning in the response.
5. `Settings.Set` each key (old values kept), `Settings.Save()`. On an exception, `Set` the old values back and answer
   500 `save_failed` with the message.
6. Response 200: `{"ok": true, "applied": [{"key": "...", "apply": "menu"}], "restartRequired": false, "backup":
   {"id": "20261026T101500123Z-r512-presettings"} | null, "warnings": [], "overwrittenCopy": null, "settings": <GET body>}`.
   Each change is logged at INFO: `admin: settings economy.versusCoinsMultiplier 1 -> 2 (menu)`.

`POST settings/reset` `{"keys": ["economy.xpMultiplier"]}`: same as a change to the schema defaults (with the same
backup rule).

`GET settings/file` → only while `Settings.IsFallback` (else 404): `text/plain; charset=utf-8`, the current file text
(≤ 256 KiB; larger: the first 256 KiB plus a marker line), with the value of every `"superPassword"` string member
replaced by `"***"` (a character scan for the member name, `:`, a JSON string literal; works on broken JSON).

`POST settings/repair` `{"confirm": "move-aside"}`: only when `fallback` is true (else 409 `invalid`). Calls
`Settings.MoveAsideAndReset(now)` (WP1): renames `settings.json` to `settings.json.broken-<yyyyMMddTHHmmssZ>`, clears
`IsFallback`, saves the in-memory defaults. Response `{"ok": true, "movedTo": "settings.json.broken-20261026T101500Z",
"restartRequired": true}`. The host keys (`embedded.*`, `gameLog.*`, `diagnostics.*`, `install.*`) are added back
by `EmbeddedHost.AddEmbeddedDefaults` at the next start.

`GET codes` → `{"codes": [{"code": "UNLOCKALL", "description": "...", "numericSuffix": true, "delivery": "ledger",
"rewards": 11368}], "pageCode": "MCVSOFFLINE", "enabled": true}` from `CouponCatalog.Describe()` (WP2 adds it: code,
description, numeric-suffix flag of every entry of `coupons.json` merged with `assets.coupons`; delivery and reward
count since the inbox-freeze fix). An inbox code above 300 rewards also has `"warning"` (redeeming it is refused), shown
under the code in the list; so has a ledger code with rewards of a quantity other than 1 (the ledger skips them).

### 4.5 Actions: save now, restart and the others

The game loop is paused while the page is open (D1), and the game never saves on its own when it is killed or
backgrounded (research 03 §7.3: autosave every `AutoSaveIntervalMinutes`, default 5; no save on app backgrounding). A
plain kill therefore loses everything since the last save. Both actions below run `ExitSequence` (WP2, §8.8), which first
closes the page and lets the game save.

`POST actions/save-now` `{"screen": "saves"}` → 501 unless `capabilities.save` and `capabilities.openPage`; 409
`restarting`/`busy` while a sequence runs; else 202 `{"accepted": true}`. Sequence: toast "MCVS Offline: saving…" →
close the browser → wait for the game loop → request the game's save → wait for it (≤ 15 s) → reopen the page with a
fresh ticket on `screen` (`#…&s=saves`; with `&r=nosave` when no save arrived). The Saves screen uses it before an export
or a manual backup ("Save the game now, then back up").

`POST actions/restart` `{"confirm": true, "reason": "settings"|"import"|"user", "restartWithoutSave": false}`:

1. 501 `not_supported` unless `capabilities.exit`; 409 `restarting` if a sequence runs.
2. Mark the module as restarting (every later admin write gets 409 `restarting`; `GET status`/`GET saves` take no
   account lock and omit levels), answer 202 `{"restarting": true}`.
3. Background thread, after 300 ms (the response is on the wire): toast "MCVS Offline: saving, then restarting the
   game…" → close the browser → wait for the game loop (≤ 8 s) → request the game's save → wait for a save of the current
   account with source `http` that starts after the request, then 1 s without another one (≤ 15 s).
4. No save arrived and `restartWithoutSave` is false: toast "MCVS Offline: the game did not save, so it was not
   restarted.", `exit.last = {result: "cancelled"}`, clear the flag. Nothing else happens.
5. Otherwise: take `LocalProfileStore.Locks.Acquire(account)` (no new save can start; an in-flight one finishes),
   `context.Logger.Flush(2000)`, `IAdminHost.Exit(reason, relaunch: true)` (§8.8). If `Exit` returns false: release the
   lock, clear the flag, toast "Restart failed: close the game yourself", `exit.last.result = "failed"`.

The confirmation on the page (Actions, `#banner-restart`, `#banner-staged`) reads: "The page closes, the game saves your
progress (last save 10:42) and restarts. This takes about 20 seconds." plus the checkbox "Restart even if the game does
not save (you may lose the progress since 10:42)" → `restartWithoutSave`.

`POST actions/crm-reload` → `CrmDocumentCache.Invalidate()` (through `context.Features.Get<CrmDocumentCache>()`),
200 `{"ok": true}` (the page: "takes effect when you close this page"). `POST actions/unlock-now` → 409 `invalid`
unless `unlockAll`; else `UnlockGranter.Enqueue(current account)`, 200 `{"queued": true}` (the page: "starts when you
close this page"; it offers Close). `POST clipboard` `{"label": "...", "text": "..."}` (text ≤ 128,000 characters,
§7.4) → `IAdminHost.CopyToClipboard`; 501 when unavailable.

### 4.6 Saves

`GET saves` → 200:

```json
{
  "current": {"account": "0123...", "source": "game", "credential": "anonymous:a1b2…f9e8"},
  "accounts": [{"account": "0123...", "fedId": "fed_id:0123...", "alias": "...", "created": "...",
                "credentials": ["anonymous:a1b2…f9e8"], "isCurrent": true, "hasGameProfile": true, "level": 37,
                "revision": 512, "modified": "...", "lastGameSave": "...", "size": 612000, "backups": 21}],
  "staged": null,
  "probation": null,
  "last": {"id": "...", "kind": "import", "result": "applied", "at": "...", "message": "...", "undoable": true}
}
```

Credentials are always masked (first 4 and last 4 characters of the user name). `level` is the profile root `level`
(as `LocalProfileStore.BackupLevel` reads it), through `IProfileAccess.Read(account, "level")`.

`GET saves/{account}/backups` → `{"account": "...", "backups": [{"id": "20261026T101500123Z-r511-auto", "date": "...",
"revision": 511, "reason": "auto", "pool": "auto"|"event"|"system"|"manual", "size": 611000, "level": 37,
"deletable": false}], "limits": {"auto": 20, "event": 20, "system": 10, "manual": 10}}` (newest first).

`POST saves/{account}/backups` `{"note": "before tuning"}` (note ≤ 80 chars, `[A-Za-z0-9 ._-]`, only logged) →
`LocalProfileStore.CreateBackup(account, "manual")` → 200 `{"backup": {...}, "asOf": "<profile modified>"}`; 404 when
the account has no stored profile; 409 `manual_full` when 10 manual backups exist ("Delete one of your backups first").
`DELETE saves/{account}/backups/{id}` → only `pool: manual` (else 400 `not_deletable`), 200 `{"ok": true}`.

`POST saves/{account}/restore` `{"backup": "<id>", "plan": "replace"|"restore-other"|"switch", "confirm": "<plan>",
"replace": false}` → stages a restore (§6.4). `plan`: `replace` when `{account}` is the current account (the only
allowed plan then); for another account `restore-other` (overwrite that account, no relink) or `switch` (overwrite it and
play it on this phone). `confirm` must equal `plan`. 200 `{"staged": <manifest>}`; 409 `staged_exists` unless `replace`;
409 `current_player_unknown` for `switch` without a known credential.

`POST saves/{account}/relink` `{"confirm": "switch", "replace": false}` → stages "play this account on this phone" for
an account that is not the current one and has a game profile (409 `no_game_profile` otherwise).

`POST saves/{account}/export` `{"target": "share"|"downloads"|"text", "format": "transfer"|"json", "partChars": 60000,
"includeSettings": false}`:

* builds the bundle (§6.1) and, for `format: transfer` (default), the transfer text (§6.2) with parts of `partChars`
  (one of 60000, 3900, 1900);
* `share`/`downloads`: writes `<data>/admin/exports/mcvs-save-<name>-<yyyyMMdd-HHmm>.txt` (transfer) or `.json` (the
  bundle; for PC tools) (newest 5 by write time kept, never the file just written), then `IAdminHost.ShareFile(path, "text/plain"|"application/json", fileName,
  "MCVS Offline save", "My MCVS Offline save (" + alias + "). Import it in MCVS Offline › Saves › Import.")`;
  `downloads` → the same call with `subject == null` ("copy only"; 501 unless `capabilities.saveToDownloads`);
* `text`: no file; the parts are returned;
* 200 `{"file": "mcvs-save-....txt"|null, "size": 107020, "asOf": "<profile modified>", "transfer": {"set": "1a2b3c4d",
  "method": "d", "chars": 107020, "parts": 2, "partChars": 60000, "texts": ["MCVS-SAVE:1:...~", "..."]|null},
  "share": {"ok": true, "method": "sheet", "publicPath": "Download/MCVS-Offline/mcvs-save-....txt", "message": "..."}|null}`.
  `texts` is filled only for `target: text`. The page shows the size, the number of parts and "Copy part k of n".
* 413 `too_large` with `details: {"limit": 999, "unit": "parts", "partChars": 1900}` when the text needs more than 999
  parts of that size (the page then offers larger parts or a file), or `"unit": "bytes"` when the binary stream is above
  16 MiB (the page offers the `.json` file). Exports share the bucket `admin.export` (6 per minute).

`GET saves/{account}/export?format=transfer|json` → the file as an attachment (`text/plain` or `application/json`,
`Content-Disposition: attachment; filename="mcvs-save-<name>-<…>.txt"`) for desktop browsers and the standalone host.

`GET saves/import/files` → `{"directory": "<data>/import", "files": [{"name": "x.txt", "size": 107020, "modified": "..."}],
"writableBy": "On Android 10 and older any file manager can put files here; on Android 11 and later use a PC (USB file
transfer or adb)."}` (only regular `*.json`/`*.txt` files ≤ 16 MiB; names `^[A-Za-z0-9._ -]{1,128}$`).

`POST saves/import/paste` `{"text": "<what the player pasted>"}` (≤ 128,000 characters, §7.4): the server extracts every
transfer part in the text (§6.2) and adds it to its set under `<data>/admin/uploads/set-<set>/` (a part that fails its
CRC is reported, not stored). Text that contains no part but starts with `{` is taken as a whole bundle or raw profile
file (§6.1). Response 200, one of:

```json
{"complete": false, "set": "1a2b3c4d", "total": 3, "have": [1, 3], "missing": [2],
 "rejected": [{"part": 2, "reason": "damaged (copied incompletely?)"}],
 "message": "Got parts 1 and 3 of 3. Paste part 2."}
```

or, when the set is complete (or the text was a whole bundle), the validation result:

```json
{
  "complete": true, "uploadId": "u-1a2b3c4d5e6f7a8b",
  "bundle": {"account": "fedcba...", "alias": "...", "level": 41, "revision": 900, "modified": "...",
             "hasGameProfile": true, "size": 640000, "messages": 2, "created": "...", "server": "0.1.0-dev"},
  "local": {"exists": true, "isCurrent": false, "revision": 120, "modified": "...", "hasGameProfile": true},
  "current": {"account": "0123...", "alias": "...", "level": 3, "source": "game"},
  "plans": ["switch"], "plan": "switch",
  "errors": [], "errorCodes": [],
  "warnings": ["This phone already has a newer save of that account (revision 950 > 900)."],
  "canStage": true, "set": "1a2b3c4d", "rejected": [], "message": "..."
}
```

`POST saves/import/validate` `{"file": "x.txt"}` → the same validation result for a file from `<data>/import/` (a
transfer text or a bundle/raw profile JSON).

`plan`: `replace` (bundle account = current account: overwrite it), `switch` (other account: write it, then relink the
current credential to it, §6.7). `errors` (§6.1) make `canStage` false, as does `switch` without a known current
credential (its sentence in `errors`, the code `current_player_unknown` in `errorCodes`). Pastes share the bucket
`admin.paste` (30 per minute). The validated upload is kept in `<data>/admin/uploads/<uploadId>.json`
for 1 hour (newest 3 kept); part sets for 1 hour (newest 3 sets).

`POST saves/import/stage` `{"uploadId": "...", "confirm": "<plan>", "replace": false, "allowOlder": false}` → 200
`{"staged": <manifest>}`. 400 `invalid` unless `confirm` equals the plan; 409 `newer_save` when the local copy of that
account has a higher revision than the bundle and `allowOlder` is false; 409 `staged_exists` unless `replace`; 409
`current_player_unknown`.

`GET saves/staged` → `{"pending": <manifest>|null, "probation": <probation>|null, "history": [<result>...]}` (newest
10; each result `{id, kind, plan, result, at, target, preBackupId, relinked, relink, probation, done, warnings, attempts,
durationMs, message, summary, source, undoOf}`, `result` one of `applied`, `failed`, `confirmed`, `reverted`,
`superseded`, credentials masked). `DELETE saves/staged` → deletes the pending manifest and its payload, 200 `{"ok": true}`.

`POST saves/staged/undo` `{"confirm": "undo", "replace": false}` → stages the inverse of the newest applied operation
(§6.7) when it is still undoable (409 `nothing_to_undo` otherwise); 200 `{"staged": <manifest>, "description": "Go back
to Player0815 (level 3) on this phone; Player4711 stays on the phone."}`.

### 4.7 Logs

`GET logs` → 200:

```json
{"directories": [{"index": 0, "path": ".../McvsOffline/logs", "kind": "primary"},
                 {"index": 1, "path": "/data/user/0/.../files/McvsOffline/logs", "kind": "fallback"}],
 "files": [{"dir": 0, "name": "server.log", "kind": "server", "size": 5242880, "modified": "..."},
           {"dir": 0, "name": "server.log.1", "kind": "rotated", "size": 5242880, "modified": "..."}],
 "mirror": "active, 3 files, last copy 12 s ago"}
```

`kind`: `server`, `bootstrap`, `game`, `rotated`, `crash`, `exception`, `threaddump`, `exit`, `unknown-endpoints`,
`other`. Sorted: current logs first, then crash/exception/exit, thread dumps, rotated, other; newest first within a
kind.

`GET logs/{name}` query: `dir` (0), `lines` (200; 1–2000), `min` (`trace`|`debug`|`info`|`warn`|`error`, default
`trace`), `q` (substring, case-insensitive, ≤ 100 chars), `raw` (1: text/plain tail of at most 512 KiB, for crash
files and dumps; redacted like everything else). Response 200:

```json
{"name": "server.log", "size": 5242880, "window": 1048576, "truncatedHead": true,
 "entries": [{"t": "2026-10-26T10:15:00.123Z", "l": "WARN", "c": "crm", "m": "CRM document: ..."}]}
```

Parsing in §7.1. `POST logs/zip` `{"scope": "recent"|"all", "note": "optional, ≤ 4,000 chars"}` → builds the zip (§7.2)
into `<data>/admin/share/`; 200 `{"file": "mcvs-logs-20261026-101500.zip", "size": 2412345, "entries": 14,
"skipped": 3, "fits": {"discord": true, "gmail": true}}`. At most one build at a time (409 `busy`), 6 per minute
(bucket `admin.zip`; `logs/share` and `logs/share-text` share `admin.share`, 6 per minute).
`POST logs/share` `{"file": "<name from logs/zip>", "target": "share"|"downloads"}` →
`IAdminHost.ShareFile(zip, "application/zip", name, "MCVS Offline logs", note)`; 200 `{"share": {...}}`.
`GET logs/bundle?scope=` → a fresh zip as `application/zip` attachment. `POST logs/share-text` `{"note": "..."}` → a
plain-text report (status summary, last 300 WARN+ entries of server.log, bootstrap.log and game.log, then of the
previous start's last file of each as "previous start" (§7.7), the newest crash
file's first 200 lines; total ≤ 100,000 characters) through `IAdminHost.ShareText`; 501 when unavailable, in which case
the page shows the same text in a read-only box for copying (returned in `text`).

## 5. Security

### 5.1 Threat model

Anything on the phone can reach `127.0.0.1:51780`: other apps (no way to tell the caller's UID: `/proc/net` is closed
to apps on Android 10+, `SO_PEERCRED` needs Unix sockets) and web pages in any browser (CSRF, and DNS rebinding, which
makes a hostile page same-origin with a hostname that resolves to 127.0.0.1). In LAN mode the listener binds `0.0.0.0`.
The Federation super credential is public, so a determined local app can already authorize as any account and write
saves through the game API; that exposure predates this design and stays (§5.5). The bar here is: **no web page can
read or change anything on any route; no app can read or change anything through `/_mcvs/*` beyond the read-only
diagnostics; no secret is ever stored where another app can read it (I2).**

### 5.2 `LocalAccessGate` (WP1) — every request

Called from `McvsServer.Handle` before `Router.Dispatch` for **every** request (`server/Core/Http/LocalAccessGate.cs`):

```csharp
public static class LocalAccessGate
{
    public const string HeaderName = "X-MCVS";                       // value must be "1"
    public static bool IsAdminPath(HttpRequest request);             // Segments.Length > 0 && Segments[0] equals "_mcvs" (OrdinalIgnoreCase)
    /// <summary>null = allowed. options: port, listener bound to loopback, lanMode, security.hostCheck, publicUrl host, admin.openVia.</summary>
    public static HttpResponse Check(HttpRequest request, GateOptions options);
    public static bool IsLoopback(IPEndPoint remote);                // null (in-process) | 127.0.0.0/8 | ::1 | ::ffff:127.x.y.z (byte check, no trimmed APIs)
    public static bool IsAllowedHost(string host, int port, bool anyPort); // 127.0.0.1 | localhost | [::1], optional ":<port>"; case-insensitive
    public static bool IsAllowedOrigin(string origin, int port);     // "http://" + allowed host + ":" + port, exact
    public static bool IsUnsafeMethod(string method);                // not GET/HEAD
    /// <summary>Header-time body limit (rule 5): called by the HTTP parser after the headers, before the body.</summary>
    public static long BodyLimit(string method, string path, HttpHeaders headers,
                                 Func<string, bool> isValidSessionCookie, Func<string, string, long> routeLimit);
}
```

Rules, in order:

1. **Federation routes** (everything not under `_mcvs`), only while the listener is bound to a loopback address, `lanMode`
   is false and `security.hostCheck` is true: a `Host` header that is present must be `IsAllowedHost(host, port,
   anyPort: true)` or equal the host part of `publicUrl`; otherwise 404 `not_found` and one WARN per minute
   `gate: refused Host '<host>' for <path> (DNS rebinding?)`. A missing Host (HTTP/1.0) is allowed. The game always calls
   `http://127.0.0.1:<port>` (args.txt, pandora); C17 checks that no WARN appears in a normal session, and the setting is
   the file-only escape hatch. In LAN mode guests use the LAN address, so the rule is off.
2. **`_mcvs` routes**: not loopback, or Host not `IsAllowedHost(host, port, anyPort: false)` → 404 `not_found` (also in
   LAN mode).
3. `OPTIONS` under `_mcvs` → 405, no CORS headers.
4. Unsafe method under `_mcvs` → requires `X-MCVS: 1`; if `Origin` is present it must be allowed (`null` is refused); if
   `Sec-Fetch-Site` is present it must be `same-origin` or `none`. Otherwise 403 `csrf`. **Single exemption** — `POST
   /_mcvs/ui` (or `/_mcvs/ui/`) is let through without `X-MCVS` when *all* hold: `admin.openVia` is `post`;
   `Content-Type` is `application/x-www-form-urlencoded`; the body is ≤ 1 KiB; `Sec-Fetch-Site` is absent or `none`;
   `Origin` is absent or `null`. That is exactly the browser-initiated form POST of `WebView.postUrl`
   (`InGameBrowser.OpenURLWithPostData`), which can add no header. The one-time ticket in the body is the protection;
   the route is rate-limited (§5.3) and its body is never logged.
5. **Header-time body limit**: for `/_mcvs/` requests the HTTP parser asks `BodyLimit` after reading the headers and
   answers 413 before reading the body when `Content-Length` (or the running chunked total) exceeds it. The limit is
   1 KiB unless the request carries a valid `mcvs_s` cookie (checked through the admin module's delegate, WP2), and then
   the route's limit from §4.2 (unknown `_mcvs` routes: 1 KiB). Federation routes keep the global 64 MiB. So a request
   without a session can never make the server buffer more than 1 KiB under `/_mcvs/`.

The denied request is logged by the normal request logger with route name `core.gate`. In-process requests
(`RemoteEndPoint == null`, e.g. `McvsServer.Handle` in tests) count as loopback; `HttpRequest.Create` sets `Host:
127.0.0.1`, which is allowed.

**Service-prefix aliases** (`server/Services/Pandora/ServicePrefixAliases.cs`): `IsSpecific` returns false for templates
whose first literal is `_mcvs`, so `/auth/_mcvs/...` never re-dispatches into a `_mcvs` route behind the gate's back.

**Change to the existing `_mcvs/*` routes:** all of them now pass rule 2 (adds the Host check to `_mcvs/status`,
`_mcvs/routes`, `_mcvs/crm`, `_mcvs/liveops`, which had no check). `POST _mcvs/crm/reload` and
`POST _mcvs/unlocks/grant` also need `X-MCVS: 1` (rule 4; blocks HTML-form CSRF; developers use
`curl -X POST -H 'X-MCVS: 1' ...`). `_mcvs/status` replaces `superPassword` in its settings snapshot with `"***"`.
**Read-only routes that stay open without a session** (loopback + Host only): `GET _mcvs/status`, `_mcvs/routes`,
`_mcvs/crm`, `_mcvs/liveops`, `_mcvs/storage`, `_mcvs/storage/profiles/{account}[?raw=1]`, `_mcvs/unlocks`,
`GET _mcvs/ui` (static page, no data), `GET _mcvs/api/v1/ping` (no data). Everything else under `_mcvs/api/` needs a
session. The raw profile holds no secret any more (I2); a local app can read the same data through the Federation API.

### 5.3 Tickets and sessions (WP2, `AdminSecurity`)

| | One-time ticket | Session |
|---|---|---|
| Value | `Ids.RandomHex(16)` (32 hex) | cookie `mcvs_s` = `Ids.RandomHex(32)` (64 hex) |
| Minted by | `IAdminPortal.MintPageUrl(trigger, screen)`, called only by the host's page opener (Account button, inbox marker through `adminlink`, Forum, redeem code, save-now reopen) and the standalone console line | `POST session` / `POST _mcvs/ui` |
| Lifetime | 120 s, single use | idle 2 h, absolute 12 h, the process |
| Limits | 16 outstanding (oldest dropped) | 4 live sessions (oldest dropped) |
| Where it travels | URL fragment `#t=` handed to the game's own WebView (never reaches the server, the request log or `Referer`); in post mode the form body of `WebView.postUrl` | `Cookie` header |

There is **no multi-use ticket**: the per-boot news ticket of revision 1 is gone; the inbox link is a fixed marker
(§9.1). Comparison is constant-time over the full string. Everything is in memory: a restart invalidates every ticket
and session.

Rate limits (sliding minute, `AdminRateLimiter`): the ticket is **validated first**, so a valid ticket always succeeds
regardless of other traffic; only failed exchanges count (5/min; then 429 for failures only, logged once). Admin writes
60/min; separate buckets of 6/min each for exports, logs zips and logs shares (file and text report), paste 30/min; one
save-now/restart sequence at a time. 429 carries
`details.retryAfterSeconds`. Session eviction cannot be forced by another app: a session needs a ticket, and tickets go
only to the WebView.

**Admin API checks** (in `AdminApi`, on top of the gate): session cookie valid (401 otherwise); writes additionally need
`Content-Type: application/json` (415) and a JSON object body (400), `X-MCVS: 1` and Origin (already by the gate),
and the per-route body limit (413). `admin.enabled = false` → every admin route answers 503 `admin_disabled` and
`IAdminPortal.Enabled` is false (no button, no inbox item, the marker hook passes through). Page responses add
`Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:;
connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`, `X-Frame-Options: DENY`,
`Referrer-Policy: no-referrer`, `Cache-Control: no-store`.

**`POST /_mcvs/ui`** (post mode): form `t=<32 hex>`, optionally `s=<screen>` and `r=nosave` (the minted URL's fragment
fields). A valid unused ticket → mark used, create a session, `303 See Other` with `Location: /_mcvs/ui/` and the
`Set-Cookie`. A used/unknown ticket with a valid cookie → `303` without a new session. Otherwise `303` without a cookie
(the page then shows `#banner-nosession`). The `Location` always carries a fragment: the screen, `s` from the form when it
is one of the six screen names and `status` otherwise, and `&r=nosave` when `r` is exactly `nosave` (anything else is left
out: `/_mcvs/ui/#s=saves&r=nosave`, `/_mcvs/ui/#s=status`). The page boots on that screen with its toast as from a ticket
URL, and without a session it shows `#banner-nosession`: a bare `/_mcvs/ui/` would be view mode (§2.4), but a post always
comes from the game. Redirecting makes the history entry a
GET: the browser's Refresh button reloads it with the cookie instead of re-POSTing a used ticket (the default
`WebViewClient.onFormResubmission` refuses to resend, which would show an error page).

### 5.4 Never logging secrets (WP1, WP5)

* **One redaction function**, `server/Core/Http/LogRedaction.cs` (WP1): `public static string Redact(string text)`.
  Replaces `#t=<8+ hex>` and its percent-encoded form `%23t%3D<hex>` → `#t=<redacted>`; `mcvs_s=<hex>` →
  `mcvs_s=<redacted>`; `"ticket":"<hex>"` (any spacing) → `"ticket":"<redacted>"`; form/query `t=<32 hex>` →
  `t=<redacted>`; the value of every form/query parameter and JSON string member named `password`, `access_token`,
  `refresh_token`, `token` or `superPassword` → its first 4 characters + `…<redacted>`; an access token of this server
  anywhere in a line, also inside a word (`mcvs` + exactly 8 hex + `.` + 32 or more hex, the shape `TokenService`
  issues; the game prints it in game.log, WR2 F6) → `mcvs…<redacted>`. The PC port adds MAC and SteamID64 rules
  (pc-port.md §4.2). Fast path: the input is returned unchanged when none of the trigger substrings occurs (ordinal
  `IndexOf`) and no access token occurs (an ordinal, case-sensitive search for `mcvs` followed by the token's shape;
  `mcvs` cannot be a trigger: every McvsOffline path contains it); otherwise a linear scan, no regex engine. `Redact`
  runs on every log line (below), so the access token rule applies to server.log, bootstrap.log and game.log on
  Android too: e.g. the 401 diagnosis line reads `token mcvs…<redacted> is not known here`.
* **In the logger, for every logger**: `Logger` (server/Core/Logger.cs) gains `Func<string, string> Redactor`, applied in
  `Logger.Write` to the message and in `LogEntry.Format` to the exception text. `McvsServer.Create` sets it on the server
  logger and `EmbeddedHost.Prepare` on the host logger (bootstrap.log, logcat lines, `MemoryLogSink`), so server.log,
  bootstrap.log, the problem sinks and status `problems` are redacted at the source.
* `RequestLogger`: for paths starting `/_mcvs/` the summary line is DEBUG (INFO when status ≥ 400) and carries no
  parameters and no body, only the body size; the DEBUG full request omits the body; `LogHandlerError` and `LogUnknown`
  omit the body and form for `/_mcvs/` paths. For every request the values of `Cookie` and `Set-Cookie` headers are
  replaced by `<redacted>` (the rest goes through the logger's redactor).
* `GameLogBridge` (WP5) applies `LogRedaction.Redact` to every game log message before `logs/game.log` (the game may log
  the URL it opens). `CrashReporter` (WP5) redacts the text of crash and exception files before writing them (they are
  mirrored to Downloads and zipped).
* `LogTail` (also `raw=1`) and the logs zip's text entries pass through `Redact` again (files written by older builds).
* Native logcat lines cannot be redacted (needs `READ_LOGS`; accepted). A one-time ticket in logcat is dead within 120 s.

### 5.5 Residual risks (accepted)

* A local app can use the Federation API with the public super credential (pre-existing). The Host check stops web pages
  (DNS rebinding), not apps.
* `_mcvs/storage/profiles/{account}?raw=1` and the other read-only diagnostics stay readable by local apps; they hold no
  secret (I2).
* A local app can fire the redeem trigger (§9.5) at most once per 30 s and only while no browser is open: the worst case
  is the game's browser showing our page, with a ticket only the WebView receives. A web page cannot: a current
  browser's request carries `Origin`, `Referer` or `Sec-Fetch-*`, and no page can give a no-cors request the game's
  `User-Agent`, which must be exactly `[<agent> ](<client id>; <datacenter>)` for a client id of the game's form (an old
  browser's `<img>` with `referrerpolicy="no-referrer"` sends none of the headers, but picking the client id `Linux` to
  match its own `(Linux; …)` no longer helps). In LAN mode the code is off: a guest's redemption runs on this phone's menu
  server and cannot be told from the player's own.
* A local app can likewise log in with the game's form, `User-Agent` and this phone's device id and so become the
  `session` source of the current player (§6.6) while the game's own credential is unknown; a web page cannot.
* A local app that wins the race to consume a one-time ticket before the WebView would need the ticket first: it is
  only in the game's memory, the Intent extra to the game's own activity and possibly native logcat (needs `READ_LOGS`).
* In post mode the ticket is in a form body for ≤ 120 s; no other process sees it.

## 6. Saves

### 6.1 Save bundle format and validation (`server/Services/Admin/Saves/SaveBundle.cs`, WP3)

```json
{
  "format": "mcvs-offline/save-bundle",
  "version": 1,
  "created": "2026-10-26T10:15:00.123Z",
  "server": {"version": "0.1.0-dev", "build": "9", "instance": "mcvs1a2b3c4d"},
  "account": {"id": "0123456789abcdef01234567", "alias": "Player4711", "created": "2026-01-02T03:04:05Z"},
  "profile": "<the profile file text exactly as on disk (storage/profiles/<key>.json), as a JSON string>",
  "profileLength": 612000,
  "profileCrc32": "1a2b3c4d",
  "messages": {"<transport>": "<messages/<account>.<transport>.json text as a JSON string>"},
  "settings": null
}
```

* `profile` is read under `LocalProfileStore.Locks.Acquire(account)` with `AtomicFile.ReadAllTextOrNull(store.PathFor
  (account))`, so the verbatim `_mcvs.gameProfile` bytes survive. `profileLength` is the UTF-8 byte count, `profileCrc32`
  the lower-case hex `Crc32.Compute` of those bytes. The export contains the progress **as of the game's last save**
  (§4.5 "Save the game now").
* `messages`: the documents of `context.OpenStore("messages")` with key `<account>.inbox` and `<account>.secured` (the
  two Hermes transports the TS reads: unclaimed coupon unlocks and season rewards), read under `store.Lock(key)`.
* `settings` (only with `includeSettings`): `Settings.Snapshot()` with `superPassword` removed; **never applied by an
  import** (informational).
* Not exported: credentials, `tokens.json`, `instance.json`, `device-id.txt`, `args.txt`, leaderboards, `social/`.

**Validation** (`SaveBundle.Parse` → `errors` and `warnings`; any error makes `canStage` false):

| Check | Error message (for the player) |
|---|---|
| `format` is `mcvs-offline/save-bundle`, `version` is the integer 1 | "This is not an MCVS Offline save (or it comes from a newer version)." |
| `account.id` matches `^[0-9a-f]{24}$` | "The save's account id is not valid." |
| `profile` is a non-empty string ≤ 12 MiB; `profileLength` and `profileCrc32` match | "The save is incomplete (copied only in part?)." |
| the profile text parses as a JSON object whose `format` is `mcvs-offline/seshat-profile` (`ProfileFile.Parse`'s "plain profile document" fallback is an **error** here), envelope `version` ≤ 1, envelope `account` equals `account.id` | "The save's profile is not an MCVS Offline profile." |
| the parsed document has a non-empty game profile (`SeshatOperations.HasGameProfile`) | "The save holds no game progress." |
| `messages` keys ⊆ {`inbox`, `secured`}; each value a string that parses to a JSON object, ≤ 4 MiB | "The save's inbox is damaged." |

Warnings (shown, staging allowed): the other `ProfileFile.Parse` warnings; a local copy of the same account with a higher
revision (staging then needs `allowOlder`); an alias that is not `AliasRegistry.IsWellFormed` or belongs to another
local account (ignored); `created` in the future. A raw profile file (`format: mcvs-offline/seshat-profile`, from the
import folder or pasted whole) becomes a bundle with that text, the envelope's `account` (same id check), no messages;
the same checks apply, the 12 MiB profile limit included (an import file may be up to 16 MiB, but its profile may not).

The summary always shows `hasGameProfile`, level and revision of the save and of the local copy, and staging requires
the confirmation that names the plan (§4.6).

### 6.2 Transfer text (`server/Services/Admin/Saves/SaveTransfer.cs`, WP3)

The text form of a bundle that survives the clipboard, chat apps and text viewers.

```
MCVS-SAVE:1:<set>:<k>/<n>:<method>:<bytes>:<crc>:<partcrc>:
<data>
~
```

* `1` format version; `<set>` 8 lower-case hex, random per export; `<k>/<n>` part number and count (1 ≤ k ≤ n ≤ 999);
  `<method>` `d` = raw deflate (RFC 1951) of the bundle's UTF-8 bytes, `s` = stored; `<bytes>` length of the binary
  stream (≤ 16 MiB); `<crc>` CRC-32 of the binary stream; `<partcrc>` CRC-32 of this part's data characters after
  whitespace removal (all CRCs `Crc32.Compute`, 8 lower-case hex).
* `<data>`: the binary stream's standard base64 (`A–Z a–z 0–9 + /`, no padding), cut into parts by characters. Space,
  tab, CR, LF and U+00A0 inside data are ignored (wrapped lines); any other character makes the part damaged. Standard
  base64 rather than base64url because `_` and `*` are markdown in Telegram and Discord and get lost when rendered text
  is copied; `+`, `/`, `:` and a single `~` are inert there.
* `~` ends a part. Text outside parts is ignored, so chat names, timestamps and the export file's instruction lines do
  not matter, and one paste may hold any number of parts in any order.

**Reading** (`ImportUploads`, server side, so a re-created browser loses nothing): every `MCVS-SAVE:` occurrence in a
pasted text is parsed; a part whose `partcrc` fails is rejected with its number; a part whose `n`, `method`, `bytes` or
`crc` differs from earlier parts of the same set is rejected ("belongs to another export"). When all `n` parts are there,
their data are concatenated in order, decoded, checked against `bytes` and `crc`, inflated with an output cap of 16 MiB
(more → 413 `too_large`) and parsed as a bundle (§6.1).

**Writing**: the `.txt` export wraps data at 76 characters per line and starts with instructions:

```
MCVS Offline save of Player4711 (level 41, revision 900), as of the game's save of 2026-10-26 10:42 UTC.
2 parts. To import it: in the game open Settings > Account > MCVS Offline > Saves > Import and paste the parts,
in any order, one or several at a time (everything from MCVS-SAVE to ~).

MCVS-SAVE:1:1a2b3c4d:1/2:d:80264:9f3e21aa:5c0ffee1:
7L0Lf9s2kij…
~

MCVS-SAVE:1:1a2b3c4d:2/2:d:80264:9f3e21aa:0badf00d:
…
~
```

"Copy part k of n" copies one part unwrapped. Part sizes: 60,000 characters (default: one or two whole parts fit one
clipboard operation, §7.4), 3,900 (Telegram's 4,096-character message limit) and 1,900 (Discord's 2,000).

**Deflate on the phone.** `System.IO.Compression.DeflateStream(Stream, CompressionMode, bool)`: the net8 BCL type on
PC; in the game the type lives in the game's own `System.dll` and its zlib calls (`CreateZStream`, `WriteZStream`,
`ReadZStream`, `CloseZStream`) are Mono internal calls implemented in the shipped `libmonosgen-2.0.so` over the system
`libz.so` (checked in the APK: `System.dll` `DeflateStreamNative`, symbol strings in `libmonosgen-2.0.so`). The
constructor with `CompressionLevel` is not used. `EmbeddedDllResolutionTests` proves the members survive the linker.
`SaveTransfer.CanDeflate` runs one compress/inflate round trip of a small buffer at first use; on any exception
(`DllNotFoundException`, `EntryPointNotFoundException`, `MissingMethodException`, `NotSupportedException`, an I/O error)
it logs one WARN and exports with method `s`. Imports accept both methods.

**Sizes**, measured with zlib level 6 on real profile files (`/home/user/mcvs-work/review/protocol/data*/storage/profiles`):

| Profile file | Bundle JSON | Deflated | Transfer text | Parts (60,000 / 3,900 / 1,900) |
|---|---|---|---|---|
| 358 KB | 393 KB | 32.6 KB | 43,472 characters | 1 / 12 / 23 |
| 591 KB | 711 KB | 80.3 KB | 107,020 characters | 2 / 28 / 57 |
| 1.4 MB (unlock-all, estimate) | ~1.7 MB | ~150–190 KB | ~200,000–260,000 characters | 4–5 / 52–67 / 106–137 |

So chat apps suit small saves only; the page shows the part count before copying and recommends "Share as a file" (any
app that can carry a `.txt`: e-mail, Drive, Telegram's Saved Messages as a file) above 4 parts. C11 measures a real
unlock-all profile.

### 6.3 Backups: pools (WP3, `ProfileBackups.cs`, `LocalProfileStore.cs`)

Backup ids on disk are `<yyyyMMddTHHmmssfffZ>-r<revision>-<token>`, where `ProfileBackups.SafeReason` keeps only
`[a-z0-9]` of the reason. Code therefore uses the tokens below (`preimport`, never `pre-import`); `Describe` splits the
id on `-` and reads the token as the third part.

| Pool | Tokens | Cap per account | Pruning |
|---|---|---|---|
| `auto` | `auto` | `storage.backups.keep` (0–200) | after each new `auto` backup, oldest `auto` beyond the cap. `keep = 0`: `Commit` takes no `auto` backup, so nothing is pruned; existing ones stay. |
| `event` | `wipe`, `delete`, `replace`, `unlockall`, `unlockcode`, any other reason a `WriteOutcome`/`IProfileAccess` gives, `backup` | 20 | after each new `event` backup |
| `system` | `preimport`, `prerestore`, `presettings`, `prerevert` | 10 | after each new `system` backup |
| `manual` | `manual` | 10 | **never**: an 11th is refused (`ManualBackupLimitException` → 409 `manual_full`); only the player's Delete removes one (I4) |

Changes:

* `LocalProfileStore.Backup(account, revision, reason)` no longer returns when `keep <= 0`: every non-`auto` backup is
  always taken (`wipe`, `delete`, `replace`, `unlockall`, `unlockcode`, the pre-* backups of §6.4). `Commit` still requests `auto` only
  when `keep > 0` and the interval is due.
* `ProfileBackups.Create(accountId, fileText, revision, reason, nowUtc, autoKeep)`: for `manual`, counts first and throws
  before writing when 10 exist; writes; then `Prune(accountId, PoolOf(token), cap)` for that pool only (`auto` with
  `autoKeep <= 0` is never pruned). `Prune(accountId, pool, cap)` deletes only that pool's oldest files.
* `public static string PoolOf(string token)`; `BackupInfo` gains `Pool` and `Deletable` (`manual`).
* `LocalProfileStore.CreateBackup(string accountId, string reason)` (public; reasons `manual`, `preimport`,
  `prerestore`, `presettings`, `prerevert`): under the account lock, copies the current file regardless of `keep`;
  returns `BackupInfo`, or null when nothing is stored.
* `LocalProfileStore.DeleteBackup(string accountId, string id)` (public): only `manual`; under the account lock; false
  for an unknown or non-manual id. `LocalProfileStore.ReadBackupText(string accountId, string id)` (public).
* Lowering `keep` deletes the oldest automatic backups beyond it at the next automatic backup (the page's confirmation
  says so). `docs/protocol/storage.md` ("keep 0 disables backups") is updated by WP8.
* As built: `keep` caps only the `auto` pool, and the interval check (`ProfileBackups.IsDue`) looks at the newest `auto`
  backup only, so an import, a settings backup or a manual one never delays the next automatic backup. `Create` and
  `Prune` take an optional `spare` id that the prune leaves alone (§6.4: the backup a staged restore copies).

### 6.4 Staged operations (`StagedOperations.cs`, WP3)

| Kind | Started from | Writes | Relinks |
|---|---|---|---|
| `import` | paste / import file (§4.6) | target profile, target messages | with plan `switch` |
| `restore` | a backup (§4.6) | target profile | with plan `switch` |
| `relink` | Saves › other accounts › "Play this account on this phone" | nothing | the current credential → target |
| `undo` | the newest applied operation (§6.7) | the target's pre-operation backup, when the operation overwrote it | back to the previous owner, when the operation relinked |

Files under `<data>/admin/staged/`:

```
pending.json                         the one pending operation (manifest)
<id>.payload.json                    bundle (import) as validated; for a restore or an undo, the text of the backup it
                                     writes, copied at staging (pool rotation can no longer take it before the restart)
probation.json                       the applied operation waiting for the game's first save (§6.5)
history/<id>.json                    results, newest 20 kept
history/<id>/messages/<t>.json       the inbox documents an import replaced
```

Manifest:

```json
{"id": "20261026T101500123Z-import", "kind": "import", "plan": "switch", "created": "...",
 "attempts": 0, "done": [],
 "target": "fedcba9876543210fedcba98",
 "relink": {"credential": "anonymous:<full name>", "from": "0123456789abcdef01234567", "to": "fedcba9876543210fedcba98"},
 "payload": "<id>.payload.json", "payloadCrc32": "1a2b3c4d",
 "restoreBackupId": null,
 "stageBackup": {"id": "20261026T101500123Z-r120-preimport", "fileCrc32": "77aa0011"},
 "preBackupId": null,
 "source": {"type": "paste", "name": null, "undoOf": null},
 "summary": {"alias": "Player4711", "level": 41, "revision": 900}}
```

**Staging** (`StagedOperations.Stage`): one pending operation at a time (409 `staged_exists` unless `replace`, which
deletes the old one and its payload). Write the payload (imports), then `pending.json` (both with `AtomicFile`), then take
a `system` backup of the target's current file (`preimport`/`prerestore`; none for `relink`) and record its id and the
CRC of the text it copied in `stageBackup`, so the player has a restore point before the restart (as built, a staged
restore's backup passes the backup being restored as `spare`, so the pool's prune cannot delete it). Log
`admin: staged import <id> for account <target> (relink anonymous:a1b2…f9e8 from <from>)`. `relink.credential` is the
current player's credential (§6.6, source `game` or `session`); `relink.from` its owner at staging.

**Apply at boot** — `SavesService.Register` calls `StagedOperations.ApplyPending(context, store, log)` synchronously.
`admin-saves` is the last module registered (§8.9) and the host calls `McvsServer.Start` only after `Create` returns, so
the game can never read the old profile or authorize with the old mapping. Every step is recorded in `done`, so a crash
in the middle resumes where it stopped:

1. Probation first (§6.5): count this start; revert when due.
2. No `pending.json` → return. Increment `attempts` and save the manifest. `attempts > 3` → history `failed: gave up
   after 3 attempts` (with `done`), delete `pending.json` and the payload, return.
3. Load and re-validate: imports re-check `payloadCrc32` and §6.1; restores read the backup text with `ReadBackupText`
   and run the same profile checks. A validation failure is not retryable: history `failed`, delete pending and payload,
   nothing written.
4. Step `account` (imports, restores): `context.Accounts.FindById(target) ?? context.Accounts.GetOrCreate("fed_id:" +
   target)` (creates the account with that exact id, verified in `AccountStore.NewIdFor`); the bundle alias is set only
   when `AliasRegistry.IsWellFormed`, no other account owns it and `Account.Data` has none.
5. Step `profile`: read the target's current file text. If its CRC equals `stageBackup.fileCrc32`, the stage-time backup
   already holds it: `BackupReason = null`, `preBackupId = stageBackup.id`. Otherwise (the TS saved after staging)
   `BackupReason = "preimport"`/`"prerestore"` and `preBackupId` = the new backup (set only if still null). Then
   `store.Commit(target, "admin." + kind, "", "admin", doc => Replace(doc, parsed))`: `Replace` sets `Root`, `Visibility`,
   `Created` (if present) from the parsed document, raises `Revision` to at least the payload's (Commit adds 1) and
   returns `new WriteOutcome { Changed = true, BackupReason = … }`. The event `storage.profile.changed` is published
   with `source: "admin"` (the unlock granter and probation ignore non-`http` saves).
6. Step `messages` (imports): for each transport in the bundle, copy the existing document (if any) to
   `history/<id>/messages/<transport>.json` with `FileMode.CreateNew` (a retry never overwrites the first copy), then
   `WriteText` the bundle's text under `store.Lock(key)`.
7. Step `relink` (when `relink` is set): `relink.credential` must start with `anonymous:`. `owner =
   Accounts.Find(credential)`: owner == target → nothing to do; owner == `relink.from` → `LinkCredential(target,
   credential)` and `Tokens.RevokeCredential(credential)` (drops the persisted grants of that credential on every
   account); otherwise skip with the warning "the phone's login changed since staging; not relinked". Logged:
   `admin: credential anonymous:a1b2…f9e8 now opens account <target> (was <from>)` (the token service's own line,
   `revoked N token(s) of anonymous:a1b2…f9e8`, is masked the same way). A resumed apply whose `done` already lists the
   relink step reads the outcome back from the accounts (whether the credential opens the target) instead of assuming it.
8. Step `probation`: an older probation is marked `superseded`; `probation.json` is written only when the phone's login
   opens the target after the apply and the operation changed what it loads there: a `replace`, or a switch or relink
   whose relink moved the login (§6.5). Otherwise any old file is deleted.
9. History `{"id", "kind", "plan", "result": "applied", "at", "target", "preBackupId", "relinked", "relink",
   "probation": true|false, "done", "warnings", "attempts", "durationMs", "message", "summary", "source", "undoOf"}`,
   delete `pending.json` and the payload. INFO `admin: staged import <id> applied to <target> in 412 ms`. A validation
   failure or giving up writes `result: "failed"`.

An I/O exception in steps 4–8: ERROR log naming the step; the manifest stays, and the next start resumes at the first
step not in `done` (after at most 3 attempts it gives up, and the history lists what was done). The relink comes after
the profile, so a failed profile write never redirects the player. The one window that repeats work (a crash between
the profile `Commit` and saving `done`) writes the same profile again with one extra `system` backup; `preBackupId`
keeps the first.

**Duration.** The apply runs on `mcvs-embedded-start` before the port opens while the game boots in parallel; nothing in
the game waits for the server (today the port opens ~0.4 s after the hook). Budget: under 2 s for a 1.4 MB profile;
the duration is logged and C11 compares it with the first Federation request in `server.log`.

### 6.5 Probation and automatic revert (`Probation.cs`, WP3)

An applied operation can produce a profile the game cannot load (another version, a hand-edited or damaged dump). The
TS then shows an error or retries, the menu is never reached, and the page (opened from the menu) is out of reach. So:

```json
{"id": "<operation id>", "kind": "import", "plan": "switch", "target": "...", "appliedAt": "...", "startsWithoutSave": 1,
 "lastStartAt": "...", "preBackupId": "...", "relink": {"credential": "anonymous:…", "from": "...", "to": "..."} | null,
 "summary": {...}}
```

* Written by apply step 8 with `startsWithoutSave = 1`, **only** for an operation after which the phone's login opens
  the target and loads something new there: a `replace`, or a switch or relink that moved the login. Not for
  `restore-other` (the phone does not load that account), a switch whose relink was skipped (the login did not move), or
  any undo (it goes back to what the phone played before; its revert would redo the operation). For those no save of the
  target would come, and a correct operation would be reverted two starts later.
* **Cleared** when a `storage.profile.changed` event with source `http` for `target` arrives (`SavesService`
  subscribes at `Register`): every TS login saves the profile right after loading it (`GameServerComponent.TakeProfileLock`
  → `SaveProfile`, docs/research/03-progression-storage.md §6 step 7), so a start that reaches the TS handshake with this
  profile clears it within seconds. History gets `confirmed`. (The event is published only for a write that changes the
  stored profile; the game posts its dump only when it changed (`ProfileSection.IsModified`), and each save dumps before
  `UpdateLoginStats` stamps `LastLoginDate`, so the TS's login save is a change.)
* At each start (apply step 1) an existing probation gets `startsWithoutSave += 1` and `lastStartAt = now`, except a
  start less than 10 s after `lastStartAt` (a double start of one restart, §8.8: not a real attempt; a crash loop takes
  longer per start, the game boots for ~10–20 s before the TS loads the profile). When it reaches **3** (the apply start
  and two more without a TS save of the target), the operation is **reverted** before the port opens: (a) with a
  `preBackupId`, take a `prerevert` backup of the target's current file and `Commit` the pre-operation backup's text to
  the target (source `admin`); (b) with a `relink`, if `Accounts.Find(credential)` is the target, `LinkCredential(from,
  credential)` and revoke. History `reverted`, WARN `admin: <id> reverted: the game did not load this save in 3 starts`,
  `#banner-probation` on the next page open. A pending operation is applied after the revert.
* Applying a new operation while a probation is pending marks the older one `superseded` in history.
* The history results are therefore `applied` (then `confirmed`, `reverted` or `superseded`), or `failed`.

### 6.6 Who is "the current player" (`CurrentPlayer`, WP2)

`AdminApi.CurrentPlayer()` returns `{AccountId, Credential, Source}`, first match wins:

1. `game`: `IAdminHost.Describe().GameCredential` → `Accounts.Find`. The host reads the game's own credential
   (`World.Authentication.AnonymousCredential`: `Args.Credential ?? anonymous:<native anonymous user name>`,
   `AuthenticationComponent.cs:213`) by reflection on the game thread every time it opens the page (§8.3). It comes from
   the game's memory, so no HTTP request can spoof it.
2. `session`: `context.Tokens.LastDirectAnonymous` (WP1: the last grant issued with `actor == null` and an `anonymous:`
   credential **that `auth/authorize` marked as the player's own login**, `Issue(…, playerLogin: true)`: a POST from
   loopback (not a LAN guest's) with a device id other than `Unknown`, the game's `User-Agent` for its `client_id` and no
   `Origin`, `Referer` or `Sec-Fetch-*` header, `GameRequest.NotTheGame`; the game's own login is exactly that, OP/Session.cs `GetAuthorizeRequest`), used
   only when it has a device id, the device id equals the host's `DeviceId` when the host knows one, and its `ClientId`
   contains `:android:` (the game's client id form) → `Accounts.Find(credential)` (follows relinks). `auth/authorize` is
   served on GET too and accepts any anonymous credential without a password, so without these filters any web page on
   the phone (a blind `<img>` or form to `127.0.0.1`) could make its own account "the current player"; the log line of
   such a grant ends with `(not taken as the player's login: <reason>)`. A local app that imitates all of it still can
   (§5.5).
3. `device`: `IAdminHost.Describe().DeviceId` → `Accounts.FindByDevice` returns exactly one account.
4. `single`: exactly one stored profile has a game profile.
5. none: `Source = null`. The Saves screen then asks the player to pick an account; `switch`, `relink` and undo of a
   relink cannot be staged (no credential).

`Credential` is known only for sources `game` and `session`. The apply re-checks the owner (§6.4 step 7).

### 6.7 Import from another phone, and the way back

The account id is `SHA-1("mcvs-offline/account/v1|" + credential)[0..24]` and the anonymous credential lives in the
game's private storage, so a reinstalled or new phone authorizes with a new credential C and gets a new account Y. A
bundle from the old phone holds account X. The import (`switch`) writes X's profile and inbox under **X** and then
`LinkCredential(X, C)`; at the next start the game authorizes with C and lands on X (the existing relink semantics,
[protocol/auth.md](../protocol/auth.md) §3.8). Alias, mailbox keys and fed id stay valid because the id is unchanged.
Y keeps its (usually empty) profile and files. Old grants of C are revoked; the client authorizes again at start and gets
a token for (X, C). Leaderboard entries keyed by X's fed id from the old phone are not imported (Q9).

**The way back** (no PC needed):

* Saves › Other accounts on this phone lists Y (and any other local account with a game profile) with **"Play this
  account on this phone"** → a staged `relink` (C → Y).
* After an applied operation, Saves and Status show **"Undo: …"** (`POST saves/staged/undo`) for the newest applied
  operation while no later operation was applied and its backups exist. The inverse: a relink back to `relink.from` when
  the operation relinked; a restore of `preBackupId` to the target when the operation overwrote an existing profile.
  The dialog describes it ("Go back to Player0815 (level 3). Player4711 stays on this phone.") and, when a restore is
  part of it, warns that the progress made since is lost and offers "Back up now" first. Undo is not offered for an undo.
* A backup of another account can be restored with plan `switch` (restore and play it here) as well as `restore-other`.

### 6.8 Which path works where

| Path | Android 5–9 | Android 10 | Android 11+ |
|---|---|---|---|
| Export: Share as a file (share sheet) | `file://` with the StrictMode relaxation (7–9) or plain (5–6); receivers need storage permission (C15) | MediaStore Downloads URI | MediaStore Downloads URI |
| Export: Save to Downloads | no (`CanSaveToDownloads` false) | yes | yes |
| Export: Copy as text | yes | yes | yes |
| Import: paste the transfer text | yes | yes | yes |
| Import: a file manager on the phone puts the file in `McvsOffline/import/` | yes | most file managers | **no** for most; Samsung My Files: device check C11; otherwise a PC (USB file transfer) or `adb push` |
| Import: from this phone's backups | yes | yes | yes |
| Import: a file in `Download/` | no (the game has no storage permission, and an uninstall clears ownership of its own old copies) | no | no |

PLAYER-SETUP (WP8) states the short version: "Moving to a new phone or reinstalling without a PC: export as a file, open
it on the new phone, copy and paste the parts into Saves › Import. With a PC, `adb push` the file into
`McvsOffline/import/`."

## 7. Logs

### 7.1 Tail parsing (`LogTail.cs`, WP3)

Read the last `min(size, 1 MiB)` bytes with `FileShare.ReadWrite`; if the window starts mid-file, drop up to the first
`\n`. Decode UTF-8 (invalid bytes replaced). An entry starts at a line matching either

* server/bootstrap: `^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z (TRACE|DEBUG|INFO |WARN |ERROR|     ) \[([^\]]*)\] `
  (`LogEntry.Format`), or
* game.log, in either of two forms (`GameLogBridge`; levels mapped `Fatal/Error → ERROR`, `Warning → WARN`,
  `Info → INFO`, `Debug/Verbose → DEBUG`):
  * build 14 and later: `^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z (\w+) \[([^\]]*)\] ` (`GameLogFormat.Line`, UTC
    like server.log; WR2 F5): the entry's `t` is that ISO time, which the page shows in local time (`fmtClock`, as for
    server.log);
  * builds up to 13: `^\d{2}:\d{2}:\d{2}\.\d{3} (\w+) \[([^\]]*)\] `: the entry's `t` is then the game's clock as
    written, `HH:mm:ss.fff` without a date, which the page shows as it is,

and continues over the following lines that match neither (exceptions, multi-line messages). Lines before the first
entry form one entry with level `?`. Filter by `min` (level `?` always shown) and `q`, return the last `lines`
entries, each message cut to 16 KiB. Hand-written matcher (character checks), no `Regex` dependency in the hot path.
Every message goes through `LogRedaction.Redact` (files written by older builds).

### 7.2 The logs zip (`LogsZip.cs` + `ZipWriter.cs`, WP3)

* Name `mcvs-logs-<yyyyMMdd-HHmmss>.zip` (phone local time), written to `<data>/admin/share/` (newest 3 by write time
  kept, never the zip just built). Built by
  `POST logs/zip`; shared by `POST logs/share` (the page shows the size in between).
* `ZipWriter`: for each entry take a snapshot (read the tail into a byte array), compute `Crc32.Compute` over the
  uncompressed bytes, compress with `DeflateStream` into a `MemoryStream` (method 8; method 0 when
  `SaveTransfer.CanDeflate` is false or the deflated data is not smaller), then write the local header with sizes and the
  data; central directory at the end; no data descriptors, no zip64. Peak memory about twice the largest entry (≤ 8 MiB).
  `File.Copy` and `ZipArchive` are not used.
* **Scope `recent`** (default), in priority order: `server.log`, `bootstrap.log`, `game.log` (tails ≤ 4 MiB each);
  their newest rotation `.1` (tails ≤ 2 MiB: the end of the previous start, where a crash happened; the host begins
  new log files at every start, §7.7);
  `crash-*`, `exception-*`, `exit-*` and `threaddump-*` files of the last 72 h (newest 10 per kind, ≤ 1 MiB each);
  `unknown-endpoints.jsonl` (tail ≤ 512 KiB). **Hard cap 8 MiB compressed**: entries are added in priority order and the
  build stops adding when the next entry would cross the cap (listed as `skipped`). Expected 1–3 MiB.
* **Scope `all`**: every file of `GET logs` in both directories (current, rotated, crash, exception, thread dumps,
  `exit-*`, `unknown-endpoints.jsonl*`), per-file cap 8 MiB (the tail), 48 MiB uncompressed total; typically 5–10 MiB
  compressed. The page warns above 10 MB ("too big for Discord") and 25 MB ("too big for Gmail").
* Plus `status.json` (the `GET status` body with `?storage=1`), `settings.json` (`Settings.Snapshot()` without
  `superPassword`), `note.txt` (the player's note, if any), `manifest.txt` (one line per file: entry name, original
  size, bytes included, `truncated`/`skipped`).
* Text entries pass through `LogRedaction.Redact` line by line before the CRC is computed; binary entries (`*.pb`) are
  copied as they are.

### 7.3 Sharing (server side)

`ShareFile` is the only way a file leaves the page on Android (D4). On Android < 10 the host may answer
`method: "none"` or use the `file://` fallback (§8.6); the page then offers the text report (`share-text`) and copy.
After starting a chooser the page says "Come back to the game within a minute" (a share target in its own task sends the
game to the background, where the freezer stops it after ~70 s; §8.5).

### 7.4 Copy in the WebView

One limit everywhere: **128,000 characters** per clipboard operation (≈ 256 KB as UTF-16 in the `ClipData` parcel, a
quarter of the ~1 MB Binder transaction buffer), for the page's copy buttons, `POST clipboard` and pastes. The page shows
the size before copying and refuses larger texts with "too long to copy: use Share, or copy it part by part". The page
tries, inside the tap handler: (1) `navigator.clipboard.writeText` (127.0.0.1 is a secure context), (2) a hidden
`<textarea>` + `select()` + `document.execCommand('copy')`, (3) `POST clipboard` (host
`ClipboardManager.setPrimaryClip` on the main looper).

### 7.5 The public mirror fix (`PublicMirror.cs`, WP5)

Root cause (D7): files created through FUSE get a MediaStore row with `IS_PENDING=1` and no `_size`; closing the file
tells MediaProvider nothing; only a scan fixes the row; an `O_TRUNC` rewrite after a scan leaves a stale size, and a
scan during the rewrite records 0. Change, Android 11+ only (the mirror's existing gate):

1. Each copy (`CopyTail`, the crash copy and the session probe) writes `<dest>.<n>.tmp` (a new name each time), closes
   it, deletes `<dest>` if it exists, renames the temp file to `<dest>` with
   `McvsOffline.Server.Core.Storage.FileSystem.Rename` (libc `rename`; `File.Move` is trimmed).
2. Then `android.media.MediaScannerConnection.scanFile(context, new String[]{dest}, new String[]{"text/plain"}, null)`
   over JNI (no permission needed; it clears `IS_PENDING` and sets `_size`). One call per copy cycle with all changed
   paths.
3. Also mirror the rotated `.1` files once per rotation, `threaddump-*.txt`, `exception-*.txt` and `exit-*-anr.txt`
   (as `.txt` copies; crash and exception copies are redacted, §5.4); `unknown-endpoints.jsonl` as `.jsonl.txt`. Like the
   logs, each is copied again while its size or time changes: a thread dump is written for up to 10 s, an ANR trace (up to
   8 MB) streams in on the exit-reasons thread, so a copy taken by an early cycle would stay truncated.
4. Pruning (at `Open`, and the diagnostics after a copy cycle that copied something): keep `logging.keepStarts` + 1
   sessions: this one and the newest others by their copies' last write time (UTC; the prefix, 8 digits, `-`, 6 digits,
   `-`, is local time and a time-zone change or the DST fall-back hour reorders it), none at all when the clock went back
   (`LogRetention.ClockWentBackNow`); `crash-*`/`threaddump-*`/
   `exception-*`/`exit-*` copies made before the oldest kept start go (§7.7), then at most 20 of them (oldest by time,
   then name). A copy left by an earlier install cannot be deleted (not ours): counted, never a prompt. One `[mirror]`
   line when something was deleted (`keeping 2 starts: deleted 9 copies (12.1 MB); 3 from an earlier install are not
   ours`).
5. Leftover `*.tmp` from a killed copy are deleted at the next start.

Confirm on the device before and after (§12, C13):
`adb shell content query --uri content://media/external/file --projection _data:_size:is_pending --where "_data LIKE '%MCVS-Offline%'"`.

### 7.6 Housekeeping of shared copies (`MediaStoreLedger.cs`, WP5)

`ShareFile` inserts MediaStore rows (`mcvs-logs-*.zip`, `mcvs-save-*.txt|json`) that the mirror's pruning never sees.
The host records each inserted content URI with its kind and time in `<data>/admin/mediastore.json` and, after each
insert, deletes the rows of that kind beyond the newest 5 with `ContentResolver.delete(uri, null, null)` (the game owns
them; no permission needed on Android 10+). Rows that no longer exist are dropped from the ledger.

### 7.7 Log retention at start (`LogRetention.cs`, build 13)

At every start the host keeps this start's logs and those of the previous N starts (`logging.keepStarts`, 0–10,
default 1) and deletes older log files in `<logs>` (`McvsOffline/logs`), the fallback log folder (`getFilesDir()/
McvsOffline/logs`; PC `%TEMP%\McvsOffline\logs`) and, through the mirror, `Download/MCVS-Offline` (§7.5). Same code on
Android and PC (EmbeddedHost); the PC has no mirror and no exit or thread-dump files.

* **A start** is one host process. `logs/starts.txt` gets one line per start (`<ISO UTC start> <platform> <version>`,
  the last 50 kept); the time is taken at the top of `EmbeddedHost.Start`, before anything of the process writes in
  `<logs>`. The page lists the file as kind "other"; the `all` zip includes it.
* **The cutoff** is the earliest recorded start time among the N starts kept (the last N lines of `starts.txt`, which are
  in file order, not time order, after a clock correction; N = 0: this start's own time). A file whose last
  write time is before it belongs to an older start and is deleted. Fewer than N starts recorded (the first start after
  the update), an unreadable `starts.txt`, or a kept start later than this one (the clock went back): no cutoff, nothing
  is deleted by age.
* **New files at every start**: `bootstrap.log`, `server.log`, `game.log` and `unknown-endpoints.jsonl` rotate a
  non-empty earlier file to `.1` before this start's first line (`SafeLogFileOptions.NewFileAtStart`; a failed rotation
  is one warning and the lines go on into the existing file). So `.1` is usually the previous start (unless the log also
  rotated by size during this start), `game.log` (no dates) is split by start, and the rule works on whole files. The rotating logs still keep at most their own file count.
* **Deleted** (an allowlist of names, never recursive): in `<logs>` the four logs' `.<digits>` files and, when no log of
  this process owns them, their current names; `crash-*.txt`, `exception-*.txt`, `threaddump-*.txt`, `exit-*-anr.txt`,
  `exit-*-tombstone.pb`; in the fallback folder `bootstrap.log`, `server.log`, `game.log` and their `.<digits>` files.
  A log that is open deletes its own older files under its I/O lock (`SafeLogFile.DeleteFilesBefore`: no race with its
  rotation), never the file it writes. Exit traces older than `LogRetention.TraceCutoffUtc` are not saved again (`ExitReasons`): the cutoff for N >= 1;
  for N = 0 the previous recorded start, so the trace of the process that just ended is still saved (and deleted with
  this start's files at the next start).
* **Never deleted**: `starts.txt`, `restart-requested.txt` (taken before), `*.tmp`, sub-folders, any other name, the
  fallback folder's parent (`hd-install/`), saves and settings, the logs zips the player saved or shared
  (`mcvs-logs-*.zip` in Downloads and `admin/share/`), `mcvs-save-*` exports.
* **Order**: Prepare reads the setting and records the start (`LogRetention.Begin`, milliseconds, UI thread) after
  `settings.json` and before `game.log` opens; the thread `mcvs-log-retention` starts right after `McvsServer.Create`
  (every log of this start is open then) and works for at most 5 s (the rest at the next start). One `[logs]` line in
  `bootstrap.log`, e.g. `keeping this start and 1 previous (since 2026-10-28T19:02:11Z): deleted 23 files, 41.6 MB
  (server.log.2-5, game.log.1, crash 1, exit 2; fallback 3)`; WARN when a file could not be deleted.
* **Share-text report** (§4.7): after the three current logs, the WARN+ entries of the previous start's last file of each:
  the lowest-numbered file last written before this start (`AdminHostInfo.StartedUtc`), "previous start"; a log that
  rotated by size during this start has its `.1` in this start. Without the start time (standalone server): `.1`,
  "previous file, usually the previous start". A player who restarted a broken game before opening the page still sends
  the broken start.

## 8. Host integration (`hosts/embedded`, Android)

### 8.1 Contracts (verbatim; WP2 commits this file first)

`server/Services/Admin/AdminContracts.cs`:

```csharp
using System.Collections.Generic;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>Trigger names (logs, status, rate-limit buckets).</summary>
    public static class AdminTriggers
    {
        public const string Account = "account";   // Settings > Account button (patch 'adminpage')
        public const string News = "news";         // inbox marker link (patch 'adminlink')
        public const string Redeem = "redeem";     // admin.redeemCode typed in Redeem code
        public const string Forum = "forum";       // patch 'forum'
        public const string Resume = "resume";     // reopened after "Save the game now"
        public const string Console = "console";   // standalone host
        public const string Test = "test";
    }

    /// <summary>
    /// What the page needs from the program hosting the server. Registered by the host with
    /// context.Features.Set&lt;IAdminHost&gt;(...) after McvsServer.Create and before Start; absent in the standalone host.
    /// Every member is thread-safe and never throws. Members return within a second, except ShareFile (10 s),
    /// WaitForGameLoop (its timeout) and Exit (returns once the exit has started; the process ends within ~2 s).
    /// </summary>
    public interface IAdminHost
    {
        AdminHostInfo Describe();
        /// <summary>Opens the in-game browser at url (posted to the game thread). False: cannot.</summary>
        bool OpenPage(string url, string trigger);
        /// <summary>Copies path into Download/MCVS-Offline/displayName and, unless subject is null, opens the share sheet.</summary>
        AdminShareResult ShareFile(string path, string mimeType, string displayName, string subject, string text);
        AdminShareResult ShareText(string subject, string text);
        bool CopyToClipboard(string label, string text);
        /// <summary>A short system toast (shown over the game after the browser closed). No-op where unsupported.</summary>
        void ShowToast(string text);
        /// <summary>Closes the in-game browser (InGameBrowser.QuitIGB on the UI thread). False: none open, or unsupported.</summary>
        bool CloseBrowser();
        /// <summary>True once the game thread has run a frame after this call; false on timeout or unsupported.</summary>
        bool WaitForGameLoop(int timeoutMs);
        /// <summary>Asks the game to save the player's profile (ForceSaveProfile, posted to the game thread). False: cannot.</summary>
        bool RequestGameSave();
        /// <summary>Ends the process through the game's own restart path (relaunch) or the host fallback (§8.8). False: nothing happened.</summary>
        bool Exit(string reason, bool relaunch);
        /// <summary>Called on every request of a valid session: the page is open, keep the watchdog paused.</summary>
        void PageAlive();
        /// <summary>WARN/ERROR lines of the host logger since boot, newest first, formatted like LogEntry.Format.</summary>
        List<string> RecentProblems(int max);
    }

    public sealed class AdminHostInfo
    {
        public string Platform { get; set; }          // "android", "directory"
        public int SdkInt { get; set; }               // Build.VERSION.SDK_INT; 0 off Android
        public string DeviceId { get; set; }
        public string GameCredential { get; set; }    // "anonymous:<name>" as the game uses it, read on the game thread; null unknown
        public string GameRoot { get; set; }
        public string LogsDir { get; set; }
        public string FallbackLogsDir { get; set; }   // internal-storage fallback of the log files, or null
        public string PublicLogsDir { get; set; }     // .../Download/MCVS-Offline, or null
        public string MirrorStatus { get; set; }      // one line
        public string WatchdogStatus { get; set; }    // one line
        public string BrowserStatus { get; set; }     // "initialized", "not initialized", "unknown"
        public bool BrowserOpen { get; set; }         // InGameBrowser.b
        public long FreeBytes { get; set; }           // free space of the game root's volume, -1 unknown
        public string RestartVia { get; set; }        // "game", "host", "none"
        public List<string> MissingJni { get; set; }  // JNI table entries that did not resolve (§8.6)
        public bool CanOpenPage { get; set; }
        public bool CanShareFile { get; set; }
        public bool CanSaveToDownloads { get; set; }  // SdkInt >= 29
        public bool CanShareText { get; set; }
        public bool CanCopy { get; set; }
        public bool CanToast { get; set; }
        public bool CanSave { get; set; }             // RequestGameSave can work
        public bool CanExit { get; set; }
    }

    public sealed class AdminShareResult
    {
        public bool Ok { get; set; }
        public string Method { get; set; }            // "sheet", "downloads", "text", "none"
        public string PublicPath { get; set; }        // "Download/MCVS-Offline/<name>" or null
        public string Message { get; set; }           // one sentence for the player

        public static AdminShareResult None(string message)
        {
            return new AdminShareResult { Ok = false, Method = "none", Message = message };
        }
    }

    /// <summary>Provided by the admin module: context.Features.Get&lt;IAdminPortal&gt;(). Thread-safe, never throws.</summary>
    public interface IAdminPortal
    {
        /// <summary>admin.enabled and the module is registered.</summary>
        bool Enabled { get; }
        /// <summary>"http://127.0.0.1:&lt;port&gt;/_mcvs/ui/#t=&lt;one-time ticket&gt;[&amp;s=&lt;screen&gt;]", or null when disabled.</summary>
        string MintPageUrl(string trigger, string screen);
        /// <summary>The fixed inbox link "http://127.0.0.1:&lt;port&gt;/_mcvs/ui/#news". Carries no secret.</summary>
        string PageMarkerUrl { get; }
        /// <summary>Ordinal equality with PageMarkerUrl; false when disabled.</summary>
        bool IsPageMarker(string url);
        /// <summary>MintPageUrl + IAdminHost.OpenPage; false without a host or when disabled.</summary>
        bool OpenPage(string trigger, string screen);
    }
}
```

`AdminApi` (WP2, used by WP3; `server/Services/Admin/AdminApi.cs`):

```csharp
public delegate HttpResponse AdminHandler(AdminCall call);

public sealed class AdminCall
{
    public HttpRequest Request { get; }
    public Dictionary<string, object> Body { get; }   // the JSON object of a write; null for GET/HEAD
    public string Route(string name);                 // percent-decoded route value
    public string Query(string name);
}

public sealed class CurrentPlayer
{
    public string AccountId { get; set; }
    public string Credential { get; set; }            // full "anonymous:<name>", or null
    public string Source { get; set; }                // "game", "session", "device", "single", or null
}

public sealed class AdminApi                          // context.Features.Get<AdminApi>() after the "admin" module registered
{
    public const string Prefix = "_mcvs/api/v1/";
    public ServerContext Context { get; }
    public Log Log { get; }                           // category "admin"
    public IAdminHost Host { get; }                   // context.Features.Get<IAdminHost>() at call time, may be null
    public GameSaveTracker Saves { get; }
    public void Get(string template, string name, AdminHandler handler);
    public void Post(string template, string name, AdminHandler handler, int maxBodyBytes);
    public void Delete(string template, string name, AdminHandler handler);
    public CurrentPlayer CurrentPlayer();
    public bool Allow(string bucket, int perMinute);  // shared rate limiter
    public bool IsRestarting { get; }                 // an ExitSequence of kind "restart" runs
    /// <summary>Set by admin-saves at registration (LocalProfileStore.CreateBackup); null until then.</summary>
    public Func<string, string, BackupInfo> CreateBackup { get; set; }   // (accountId, reason) → backup or null
}

public sealed class GameSaveTracker                   // subscribes to storage.profile.changed; source "http" only
{
    public DateTime? LastGameSave(string accountId);  // this process
    /// <summary>True when a save of accountId began after sinceUtc and none followed for quietMs; false on timeout.</summary>
    public bool WaitForSave(string accountId, DateTime sinceUtc, int timeoutMs, int quietMs);
}

public static class AdminJson
{
    public static HttpResponse Ok(object body);
    public static HttpResponse Error(int status, string code, string message, Dictionary<string, object> details = null);
}
```

`AdminApi.Get/Post/Delete` prefix the template with `Prefix`, register the route's body limit for the header-time check
(§5.2 rule 5), run the session/CSRF/size/`admin.enabled`/restarting checks and only then the handler; a handler exception
becomes 500 `internal` (logged by the router without body). As built, `AdminApi` also has `DefaultBodyLimit` (1 KiB),
`Enabled`, `ExitState` (`idle`, `saving`, `restarting`; the saves routes answer 409 while it is not idle),
`RateLimited(bucket, perMinute)` (the 429 answer), `SetStatusSection(name, provider)` (admin-saves adds `staged` to
`GET status`) and `MaskCredential` (`Credential.Mask` in Core: first and last 4 characters of the user name).

`McvsOffline.AdminHooks` (WP5, `hosts/embedded/Admin/AdminHooks.cs`; the patch contract of §9.2 and §9.3):

```csharp
namespace McvsOffline
{
    public static class AdminHooks
    {
        public const string ButtonAction = "mcvs_offline";
        public const string ButtonLabel = "MCVS Offline";
        public const string ButtonIcon = "Menus/assets/png/icons/Settings.png";

        /// <summary>Game thread (Flash native call). Returns a List&lt;object&gt; (ASValue.SetObject turns an IList into an
        /// AS array; any other IEnumerable would become an opaque managed object). Never throws.</summary>
        public static IEnumerable<object> AccountMenuButtons(IEnumerable<object> buttons);

        /// <summary>Game thread. world: the caller's Gameloft.Engine.World (as object), may be null. Never throws.</summary>
        public static void OpenPage(string trigger, object world);

        /// <summary>Game thread, first thing in BrowserComponent.OpenUrl (patch 'adminlink'). browserComponent: the
        /// Gameloft.Engine.BrowserComponent (as object). True: url was the page marker and was handled (the original
        /// body is skipped). False for every other url, when the portal is disabled or absent, and on any error.</summary>
        public static bool OpenIfPageMarker(string url, object browserComponent);
    }
}
```

`AccountMenuButtons`: when `IAdminPortal.Enabled` is false (or the server is not running) return `buttons` unchanged;
otherwise return the same `List<object>` (or a new list with every item) with one more entry, a
`Dictionary<string, object>` `{"customAction": "mcvs_offline", "isClickable": true, "label": ButtonLabel, "icon":
ButtonIcon}` (`ASValue.SetObject` turns an `IDictionary` into a real AS object; the game's own entries are anonymous
types and become managed objects with the same property names). The entry goes last. Build 13: with
`admin.accountMenu.hideDeadButtons` (default on) the dead entries are dropped first, also when the page is off, and the
buttons left are centred (§9.2).

`OpenIfPageMarker`: `url == null` or not `IsPageMarker(url)` → false (an ordinal compare, no allocation: this runs for
every URL the game opens). Marker: `world = browserComponent.World` (reflection, cached); a server World (`IsServer !=
false`) → true (ignored, as the original body returns there); otherwise `OpenPage(AdminTriggers.News, world)` → true.

### 8.2 `AdminHost` (WP5, `hosts/embedded/Admin/AdminHost.cs`)

Implements `IAdminHost` with the `EmbeddedHost` and an optional `IAdminPlatform`:

```csharp
// hosts/embedded/IEmbeddedPlatform.cs (WP5 adds)
public interface IAdminPlatform
{
    int SdkInt { get; }
    bool OpenInGameBrowserJni(string url);                     // SetParameters("EN") + OpenURL(url, true)
    bool OpenInGameBrowserWithPostData(string url, string postData); // SetParameters("EN") + OpenURLWithPostData(url, postData)
    bool IsInGameBrowserOpen();                                // InGameBrowser.b (public static boolean)
    bool QuitInGameBrowser();                                  // InGameBrowser.QuitIGB()
    AdminShareResult ShareFile(string path, string mimeType, string displayName, string subject, string text);
    AdminShareResult ShareText(string subject, string text);
    bool CopyToClipboard(string label, string text);
    void ShowToast(string text);                               // Toast.makeText(context, text, LENGTH_LONG).show() on the UI thread
    bool ExitViaGame(bool relaunch);                           // MainActivity.d.a(relaunch) on the UI thread (§8.8)
    bool ExitViaHost(bool relaunch);                           // alarm, finishAndRemoveTask, killProcess (§8.8)
    string PublicDownloadsDir();                               // Environment.getExternalStoragePublicDirectory("Download") + "/MCVS-Offline"
    long FreeBytes(string path);                               // StatFs.getAvailableBytes, -1 on failure
    List<string> MissingJniMembers();                          // AndroidJni table entries that did not resolve
}
```

`AndroidPlatform` implements it (JNI code in new `hosts/embedded/Android/AndroidShare.cs`, `AndroidExit.cs`,
`AndroidJni.cs`, `MediaStoreLedger.cs`, `#if MCVS_EMBEDDED`); `DirectoryPlatform` does not, so on PC/tests every
capability is false and the share methods return `AdminShareResult.None(...)`.

Game-thread work is reflection only, in `hosts/embedded/Admin/GameReflection.cs` (no reference to Gameloft.Engine, as
the watchdog does): `Gameloft.Engine.GameApplication.Instance`, its `World` and `Context` (the `SynchronizationContext`
the game thread drains each frame); `World.Browser`, `BrowserComponent.IsInGameBrowserInitialized`,
`BrowserComponent.OpenUrl(string, bool)`; `World.Authentication.AnonymousCredential` (string conversion) for
`GameCredential`; `World.EnumerateActiveComponents(Type, ComponentGroupOptions)` with `Game.PlayerProfileComponent` and
its `ForceSaveProfile(CancellationToken)` for `RequestGameSave`. Every member is resolved once and verified against the
real assemblies by `RealGameReflectionTests` (WP5, MCVS_REFS).

* `WaitForGameLoop(ms)`: post a callback to `GameApplication.Instance.Context` and wait for it to run (≤ ms).
* `RequestGameSave()`: post to the game thread: the client World's `PlayerProfileComponent.ForceSaveProfile(
  CancellationToken.None)`. It is a `[Remote(Any, Any)]` RPC: on the client it sends the game's own remote request to the
  TS, which runs `SaveProfile` → `DoSaveProfile` → the dump POST (the account-link save path,
  `PlayerProfileComponent.cs:2722-2726, 5304-5314`). Faults of the returned task are logged, never thrown.
* `CloseBrowser()`: `QuitInGameBrowser()` and `PauseFor(0)` (ends the watchdog lease).
* `Exit(reason, relaunch)`: write `logs/restart-requested.txt` (`<iso time> <reason>`), flush the host and game logs
  (2 s), then by `admin.restartVia` (§8.8).

`EmbeddedHost` wiring (WP5): `RunServer` sets `created.Context.Features.Set<IAdminHost>(adminHost)` right after
`McvsServer.Create` and before the first `Start` attempt; `Prepare` adds a `MemoryLogSink(LogLevel.Warn, 100)` to the
host logger (for `RecentProblems`), sets the host logger's redactor (§5.4), resolves the JNI table and keeps the platform
reference. **Invariant:** the host never calls `Settings.Save()` after `McvsServer.Create` (today it saves only in
`AddEmbeddedDefaults`, before), so the page's writes to the server copy are never overwritten. At start the host reads
and deletes `logs/restart-requested.txt` and logs `[boot] previous exit: restart requested from the settings page
(<reason>)`.

### 8.3 Opening the in-game browser on the game thread

`PageOpener` (WP5, `hosts/embedded/Admin/PageOpener.cs`):

* `IAdminHost.OpenPage(url, trigger)` (server thread, e.g. redeem code, save-now reopen): post a callback to
  `GameApplication.Instance.Context`; in it, `world = GameApplication.Instance.World`, then `OpenWithWorld(world, url)`.
  Returns true once posted.
* `AdminHooks.OpenPage(trigger, world)` and `OpenIfPageMarker` (already on the game thread): read `GameCredential`, mint
  the URL, then `OpenWithWorld(world, url)`.
* `OpenWithWorld` by `admin.openVia` (read from the server's settings at call time):
  * `auto` (default): `browser = world.Browser`; if `browser.IsInGameBrowserInitialized` → `browser.OpenUrl(url, true)`
    (`BrowserComponent.OpenUrl(string, bool)`: returns early on server Worlds, mutes game sounds through `OnIgbStart`,
    calls native `BrowserComponent_OpenURL_private` → Java `InGameBrowser.OpenURL(String, boolean)`); else JNI. The
    `adminlink` prefix sees this URL but passes it through (it carries a ticket, so it is not the marker).
  * `game`: only the managed path; `jni`: only the JNI path.
  * `post`: JNI `InGameBrowser.SetParameters("EN")` then `InGameBrowser.OpenURLWithPostData("http://127.0.0.1:<port>/_mcvs/ui",
    "t=<ticket>[&s=<screen>][&r=nosave]")` (ticket, screen and result are taken from the minted URL's fragment, §5.3;
    without `SetParameters` `onCreate` closes the activity with `NotItitialized`, `InGameBrowser.java:134-138`). `WebView.postUrl` sends it as a browser-initiated
    `application/x-www-form-urlencoded` POST; the server answers `303` with the cookie (§5.3). For a WebView/native path
    that loses fragments (device check C3).
* JNI path: `com/gameloft/ingamebrowser/InGameBrowser.SetParameters(Ljava/lang/String;)V` with `"EN"`, then
  `OpenURL(Ljava/lang/String;Z)V` with `true`. `OpenURL` is a no-op while a browser is already shown (its
  `AtomicBoolean k`, reset only in `b()` or when `startActivity` throws; C3 opens twice). Game sounds are not muted on
  this path (the native exit callback still unmutes); whether closing a JNI-opened browser is safe while the native
  browser was never initialised is device check C3.
* Every open starts the watchdog lease (§8.5) and logs `admin: page opened (trigger account, via game|jni|post)`.

### 8.4 Two `Settings` instances

| Copy | Loaded | Read by | Written by |
|---|---|---|---|
| Host (`EmbeddedHost.Settings`) | `EmbeddedHost.Prepare`, UI thread, before args.txt | host keys at boot only: `gameLog.*`, `diagnostics.*`, `embedded.*`, `install.*`, `graphics.*`, `hd.*`, `port`, `lanMode`, `bind` | `AddEmbeddedDefaults` (missing keys only, before the server loads; never when `IsFallback`) |
| Server (`ServerContext.Settings`) | `McvsServer.Create` | every module, on use; the host's `PageOpener`/`AdminHost` for `admin.openVia`/`admin.restartVia`; the host's `NotificationPrompt` for `notifications.*` | the page (`Set` + `Save`), `MoveAsideAndReset`, `NotificationPrompt` (`notifications.requestedAt`, `SetAndSave`) |

No reload, no watcher. The `restart` class in §3 is exactly the set of keys that only the host copy or boot-time code
reads; changing them through the server copy is harmless until the restart applies them.

### 8.5 Watchdog while the page is open

The game loop stops while the in-game browser is in front (main activity paused, native pause), so
`GameThreadWatchdog` would report STALLs at 5/15/60/180 s and take thread dumps. WP5 adds to `GameThreadWatchdog`:

```csharp
/// <summary>Treat the game thread as paused (not stalled) until now + ms; the latest call wins; 0 ends it. Thread-safe.</summary>
public void PauseFor(int ms, string reason);
```

In `Run`, while `Environment.TickCount - pausedUntil < 0` the loop behaves as for `suspendUpdateInBackground` (sets
`lastBeat = now`, no snapshot, no dump) and logs once `[watchdog] paused: <reason>` and once `[watchdog] resumed`, at
INFO (the host logger's WARN lines are the Status screen's recent problems; stall snapshots stay WARN).
`AdminHost` calls `PauseFor(45000, "MCVS Offline page open")` on every open and every `PageAlive()` (the page pings every
10 s, at once when it becomes visible again, and every API call counts). Before starting a chooser it calls
`PauseFor(600000, "share target")`: a share target in its own task sends the game to the background, where the game
loop is paused anyway; the first `PageAlive` after the player returns brings the lease back to 45 s. `CloseBrowser`
ends the lease. When the browser closes by itself the pings stop and the lease ends ≤ 45 s later; the game thread beats
again immediately, so normal detection resumes. The MediaStore URI handed to the share target stays readable through
MediaProvider even while the game is frozen (an advantage over a FileProvider, which would run in the game process).

### 8.6 Share sheet, clipboard, toast over JNI, and the JNI table (`AndroidShare.cs`, `AndroidJni.cs`, WP5)

All calls through `Android.Runtime.JNIEnv` (the trimmed Mono.Android has no `Intent` constructors; `JNIEnv` is
complete; `JNIEnv.FindClass` uses the app class loader, so it works from server threads). Context:
`Android.App.Application.Context.Handle`; activity: `com/gameloft/android/GLUtils/SUtils.getActivity()`; UI thread:
`new Android.OS.Handler(Android.OS.Looper.MainLooper).Post(Action)`. Every wait for the UI thread is bounded (`ShareText`
and `CopyToClipboard` 0.8 s + 0.1 s, within the one-second contract; the chooser 3 s; the game's restart 2 s), and an
action the UI thread has not started by then is withdrawn and never runs (`UiCall`): no sheet opens and no copy lands
after the page was told it failed. One that started but has not ended is reported as such: `CopyToClipboard` false
(unknown), `ShareText` `Ok` false with `Method` `text` (the sheet may still open; the share lease stays).

| Android | `ShareFile` |
|---|---|
| 10+ (API ≥ 29) | MediaStore Downloads: `ContentValues` {`_display_name` = displayName, `mime_type`, `relative_path` = `Download/MCVS-Offline/`, `is_pending` = 1} → `ContentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)` → `openFileDescriptor(uri, "w")` → `getFd()` → libc `write` loop (P/Invoke like `FileSystem`) → `close()` → `update(uri, {is_pending = 0}, null, null)` (the update scans: correct `_size`) → record in the ledger (§7.6). Unless `subject == null`: `Intent("android.intent.action.SEND")`, `setType(mime)`, `putExtra(EXTRA_STREAM, uri)`, `putExtra(EXTRA_SUBJECT, subject)`, `putExtra(EXTRA_TEXT, text)`, `setClipData(ClipData.newRawUri("", uri))`, `addFlags(FLAG_GRANT_READ_URI_PERMISSION = 1)`, `Intent.createChooser(intent, subject)`, `activity.startActivity(chooser)` on the UI thread. Result `sheet` (or `downloads`), `PublicPath = "Download/MCVS-Offline/<name>"` (MediaProvider may add " (1)" on a name clash: read back `_display_name`). On failure: delete the row (`ContentResolver.delete(uri, null, null)`). |
| 7–9 (API 24–28) | `StrictMode.setVmPolicy(new StrictMode.VmPolicy.Builder().build())` once (disables the FileUriExposed check; relies on I1), `Uri.fromFile(new File(path))` (the file under `Android/data/.../McvsOffline/admin/`), same `ACTION_SEND` + chooser. Result `sheet`, `PublicPath = null`. Receivers need storage permission to read it (best effort; device check C15). |
| 5–6 (API 21–23) | `Uri.fromFile` directly, same intent. |

`ShareText(subject, text)`: the game's own `com/gameloft/android/GLUtils/SUtils.shareInfo(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)V`
(subject, text, chooser title), on the UI thread; text ≤ 100,000 characters. Result `text`.

`CopyToClipboard`: `getSystemService("clipboard")` → `android/content/ClipData.newPlainText(CharSequence, CharSequence)`
→ `ClipboardManager.setPrimaryClip(ClipData)` on the UI thread; ≤ 128,000 characters (§7.4).

`ShowToast`: `android/widget/Toast.makeText(Context, CharSequence, 1)` → `show()` on the UI thread.

**One JNI table.** Every (class, member, descriptor) the host uses is one entry of a static table in `AndroidJni.cs`
(`{Class, Name, Descriptor, IsStatic, IsField, MinSdk}`); every call looks its member up through the table (cached
`jmethodID`/`jfieldID`); nothing is written inline elsewhere. `Prepare` resolves the whole table once (entries with
`MinSdk` above the device's are skipped) and reports misses in `Describe().MissingJni`; a capability whose members are
missing is off. `JniTableTests` (WP5, net8) checks the framework entries against `android.jar` (path from
`MCVS_ANDROID_JAR`, or `platforms/android-29/android.jar` next to `TestEnvironment.BuildToolsDir`; a small class-file
reader in the test) and the game entries (`com/gameloft/...`) against the APK's `classes*.dex` (`TestEnvironment.GameApk`,
through `dexdump` from the build tools); skipped when absent, required with `MCVS_REQUIRE_REAL_CLIENT=1`.

The table (none needs a manifest change):

```
android/provider/MediaStore$Downloads             EXTERNAL_CONTENT_URI  Landroid/net/Uri;      (static field, API 29)
android/content/Context                           getContentResolver()Landroid/content/ContentResolver;
                                                  getSystemService(Ljava/lang/String;)Ljava/lang/Object;
                                                  getPackageManager()Landroid/content/pm/PackageManager;
                                                  getPackageName()Ljava/lang/String;
android/content/ContentValues                     <init>()V  put(Ljava/lang/String;Ljava/lang/String;)V  put(Ljava/lang/String;Ljava/lang/Integer;)V
java/lang/Integer                                 valueOf(I)Ljava/lang/Integer;
android/content/ContentResolver                   insert(Landroid/net/Uri;Landroid/content/ContentValues;)Landroid/net/Uri;
                                                  openFileDescriptor(Landroid/net/Uri;Ljava/lang/String;)Landroid/os/ParcelFileDescriptor;
                                                  update(Landroid/net/Uri;Landroid/content/ContentValues;Ljava/lang/String;[Ljava/lang/String;)I
                                                  delete(Landroid/net/Uri;Ljava/lang/String;[Ljava/lang/String;)I
                                                  query(Landroid/net/Uri;[Ljava/lang/String;Ljava/lang/String;[Ljava/lang/String;Ljava/lang/String;)Landroid/database/Cursor;
android/database/Cursor                           moveToFirst()Z  getString(I)Ljava/lang/String;  close()V
android/os/ParcelFileDescriptor                   getFd()I  close()V
android/content/Intent                            <init>(Ljava/lang/String;)V  setType(Ljava/lang/String;)Landroid/content/Intent;
                                                  putExtra(Ljava/lang/String;Landroid/os/Parcelable;)Landroid/content/Intent;
                                                  putExtra(Ljava/lang/String;Ljava/lang/String;)Landroid/content/Intent;
                                                  addFlags(I)Landroid/content/Intent;  setClipData(Landroid/content/ClipData;)V
                                                  createChooser(Landroid/content/Intent;Ljava/lang/CharSequence;)Landroid/content/Intent; (static)
android/content/ClipData                          newRawUri(Ljava/lang/CharSequence;Landroid/net/Uri;)Landroid/content/ClipData;
                                                  newPlainText(Ljava/lang/CharSequence;Ljava/lang/CharSequence;)Landroid/content/ClipData;
android/content/ClipboardManager                  setPrimaryClip(Landroid/content/ClipData;)V
android/widget/Toast                              makeText(Landroid/content/Context;Ljava/lang/CharSequence;I)Landroid/widget/Toast; (static)  show()V
android/app/Activity                              startActivity(Landroid/content/Intent;)V  finishAndRemoveTask()V
android/net/Uri                                   fromFile(Ljava/io/File;)Landroid/net/Uri;  parse(Ljava/lang/String;)Landroid/net/Uri;  toString()Ljava/lang/String;
java/io/File                                      <init>(Ljava/lang/String;)V
android/os/StrictMode                             setVmPolicy(Landroid/os/StrictMode$VmPolicy;)V
android/os/StrictMode$VmPolicy$Builder            <init>()V  build()Landroid/os/StrictMode$VmPolicy;
android/media/MediaScannerConnection              scanFile(Landroid/content/Context;[Ljava/lang/String;[Ljava/lang/String;Landroid/media/MediaScannerConnection$OnScanCompletedListener;)V
android/os/StatFs                                 <init>(Ljava/lang/String;)V  getAvailableBytes()J
android/content/pm/PackageManager                 getLaunchIntentForPackage(Ljava/lang/String;)Landroid/content/Intent;
android/app/PendingIntent                         getActivity(Landroid/content/Context;ILandroid/content/Intent;I)Landroid/app/PendingIntent;
android/app/AlarmManager                          set(IJLandroid/app/PendingIntent;)V
java/lang/System                                  currentTimeMillis()J
android/os/Process                                myPid()I  killProcess(I)V
com/gameloft/android/GLUtils/SUtils               getActivity()Landroid/app/Activity;  shareInfo(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)V
com/gameloft/ingamebrowser/InGameBrowser          SetParameters(Ljava/lang/String;)V  OpenURL(Ljava/lang/String;Z)V
                                                  OpenURLWithPostData(Ljava/lang/String;Ljava/lang/String;)V  QuitIGB()V
                                                  b Z (public static field: a browser is shown)
com/gameloft/android/MainActivity                 d Lcom/gameloft/android/MainActivity; (public static field)  a(Z)V
```

Every JNI helper deletes its local references, catches everything and returns a failure result. The embedded build
and `EmbeddedDllResolutionTests` prove the managed members exist in the game's trimmed `Mono.Android`.

### 8.7 Log mirror and share files

The mirror (§7.5) and `ShareFile` both write `Download/MCVS-Offline/`; the mirror by direct path on Android 11+ (with
scan), `ShareFile` through MediaStore on Android 10+. Names never clash (`<session>-…txt` vs
`mcvs-logs-…zip`/`mcvs-save-…txt|json`). `CanSaveToDownloads` is `SdkInt >= 29`; the page hides "Save to Downloads"
otherwise. Shared copies are pruned through the ledger (§7.6).

### 8.8 Save-then-exit (`ExitSequence`, WP2; host primitives, WP5)

Why not just kill: the game never saves on a kill (research 03 §7.3), the game loop is paused while the page is open,
and the kill itself misbehaves with the browser on top. With the browser in front, `MainActivity` is paused, so
`finish()` would not run `onPause` again and the game's `c()` never runs; and when a process dies with the browser
(resumed, no saved state) and `MainActivity` (stopped, saved state) in its task, Android drops the browser and
immediately re-creates `MainActivity` in a new process, which a relaunch alarm with `CLEAR_TASK` then finishes mid-boot
(its `onPause` → `c()` kills that process again, possibly inside `ApplyPending`).

**Sequence** (`ExitSequence.Run`, background thread; every step logged as `admin: exit <kind>: <step>`; the order is
unit-tested with a recording fake `IAdminHost`; timings in `ExitTimings`):

| # | Step | save-now | restart |
|---|---|---|---|
| 1 | `ShowToast` ("MCVS Offline: saving…" / "…saving, then restarting the game…") | ✓ | ✓ |
| 2 | `CloseBrowser()` (QuitIGB; the page is gone from here on) | ✓ | ✓ |
| 3 | `WaitForGameLoop(8000)`: `MainActivity` resumed, the game thread runs frames | ✓ | ✓ |
| 4 | `t0 = now`; `RequestGameSave()` | ✓ | ✓ |
| 5 | `Saves.WaitForSave(account, t0, 15000, 1000)` (the TS's dump POST and merge) | ✓ | ✓ |
| 6 | reopen: `IAdminPortal.OpenPage(Resume, screen)` (`&r=nosave` when step 5 failed) | ✓ | – |
| 7 | no save and not `restartWithoutSave` → toast "…did not save, so it was not restarted.", `exit.last = cancelled`, stop | – | ✓ |
| 8 | `LocalProfileStore.Locks.Acquire(account)`, `Logger.Flush(2000)`, `IAdminHost.Exit(reason, relaunch: true)`; false → release, toast "Restart failed: close the game yourself", `exit.last = failed` | – | ✓ |

**`IAdminHost.Exit` on Android** (`AndroidExit.cs`), by `admin.restartVia`:

* `game` (default) — the game's own restart path. On the UI thread: if `InGameBrowser.b` is still true, `QuitIGB()` and
  wait 500 ms; then `MainActivity.d.a(true)` (`MainActivity.java:315-318`: sets the restart flag `e` and posts
  `finish()`). `MainActivity` is resumed, so `finish()` runs `onPause`, which sees `isFinishing()` and calls `c()`
  (`MainActivity.java:147-157`): `AlarmManager.set(RTC, now + 900, PendingIntent.getActivity(this, 0, new
  Intent(getIntent()), 32768))` and `Process.killProcess(myPid())` (`:107-115`). The task has no other activity, so
  Android re-creates nothing; the alarm starts the next process. If the process is still alive 3 s later (e.g.
  `MainActivity.d` was null or not resumed), fall through to `host`. If the UI thread has not started `d.a(true)` within
  2 s, the call is withdrawn and `host` runs at once instead: the host's restart never starts next to a game restart
  that can still run (only the 3 s fallback follows one that did).
* `host` — on the UI thread, in this order: `AlarmManager.set(RTC, now + 1000, PendingIntent.getActivity(context,
  0x4D435653, launchIntent, FLAG_CANCEL_CURRENT | (sdk ≥ 23 ? FLAG_IMMUTABLE : 0)))` with
  `getLaunchIntentForPackage(getPackageName())` + `FLAG_ACTIVITY_NEW_TASK | FLAG_ACTIVITY_CLEAR_TASK`; then
  `activity.finishAndRemoveTask()` (no activity survives to be re-created; a resumed `MainActivity` may run `c()` and
  kill at once, which is fine); then `Process.killProcess(myPid())` 200 ms later.
* `none` — `CanExit` false; the page offers no restart, only "close the game yourself".

Whether the relaunch happens on Android 10+ (background activity start rules for an alarm of a dead app, I1') and how
many processes and boots one restart produces is device check C12; the decision between `game` and `host` as the
default is taken there, not both. The page always says "If the game does not come back by itself, start it again from
the home screen." Should one restart still produce an extra short-lived start, nothing is lost: `ApplyPending` resumes
from `done` within its 3 attempts (§6.4), and probation does not count a start less than 10 s after the previous one
(§6.5).

### 8.9 Boot order (embedded)

1. `Bootstrap.Init` → `EmbeddedHost.Prepare` (UI thread): host settings, `AddEmbeddedDefaults`, installer data,
   args.txt, host logger redactor, JNI table; thread `mcvs-embedded-start`.
2. `RunServer` → `McvsServer.Create`: server settings, logger redactor, `ServerContext` (accounts.json, tokens.json),
   core routes, `ServiceRegistry.RegisterAll`: … `storage` (creates `LocalProfileStore`; reads no profile) … `crm`
   (warm-up thread loads only the preconfiguration, then waits up to 2 s for the portal, §9.1) … `unlocks`, **`admin`**
   (security, portal, `GameSaveTracker`, boot settings snapshot), **`admin-logs`**, **`admin-saves`** (last: probation,
   then `StagedOperations.ApplyPending`). As built in `ServiceRegistry.RegisterAll`; a second registration of an admin
   module is ignored, and the overload `RegisterAll(router, context, include)` leaves modules out (tests: a server
   without the admin modules).
3. Host: `Features.Set<IAdminHost>(adminHost)`, then `created.Start()` → the port opens. Until here the game could not
   reach the server, so it never saw the pre-import state.
4. Host diagnostics (watchdog, mirror with the new scan logic, restart marker).

### 8.10 Standalone host and the Steam port

The standalone host registers no `IAdminHost`: status `host` is null, share/restart/save/clipboard answer 501, the page
offers the download routes (`GET saves/{account}/export`, `GET logs/bundle`), which work in desktop browsers, and the
import folder `<data>/import/` is on the PC's disk. WP8 adds to `hosts/standalone/Program.cs` one console line at start,
written with `Console.WriteLine` (never the logger): `Settings page: http://127.0.0.1:<port>/_mcvs/ui/#t=<ticket>` from
`MintPageUrl("console", null)`. For Steam only the "open" action changes (system browser through `Process.Start`); Win32
has no freezer and the engine pauses only Worlds.

## 9. Triggers

### 9.1 CRM `newsfeeds` inbox item (WP7; full page with `adminlink`)

Client side (evidence): `StandardCrm.DeserializeNewsFeed` reads `newsfeeds` when present
(`Gameloft.OnlinePlatform/StandardCrm.cs`); `NewsFeedData` maps `asset`, `body` (`.Trim()`), `redirect`, `title`
(`.Trim()`), `youtube`, `news_id`, `timestamp` (epoch seconds through `Convert.ToInt64`), `end_date` (ISO 8601);
missing strings default to `""`. On the menu server `InboxComponent.OnCrmDataSet` (only when
`World.Synchronization.IsServer`) removes inbox news messages whose id is no longer listed, then for each item with
`timestamp + 30 days ≥ now` creates `InboxMessage("newsfeed_" + news_id)` (expiry `end_date` or now + 30 days) or
refreshes title/body/asset/`RedirectLink` of the existing one. `From` is the part of `news_id` (split on `_`) that
contains `From`, minus `From`. Flash `Inbox.gotoLink` opens any link starting with `http` through
`Native.OpenUrl(link, IsKayac())` → `UIComponent.OpenUrl(url, false)` (or `MenuComponent.NativeOpenUrl`) →
`BrowserComponent.OpenUrl(url, …)`.

Document shape added at the top level of the CRM document:

```json
"newsfeeds": [
  {
    "news_id": "mcvsoffline_FromMCVS",
    "title": "MCVS Offline",
    "body": "Settings, saves and logs of your offline game. Tap the button to open them.",
    "redirect": "http://127.0.0.1:51780/_mcvs/ui/#news",
    "asset": "",
    "youtube": "",
    "timestamp": 1761523200
  }
]
```

* `redirect` is `IAdminPortal.PageMarkerUrl`: fixed per port, **no secret** (I2). The marker contains the port the server
  listens on; the cache key includes it, so a document built before `Start` (port 0 in tests) is rebuilt.
* The warm-up thread waits up to 2 s (`CrmDocumentCache.PortalWaitMs`) for `IAdminPortal` while `crm.newsfeedLink` is on:
  the admin module registers after the CRM module, and without a data pack the warm-up would otherwise build its
  document before the portal exists, leaving the TS's first request to rebuild it inside its 1 s `WaitForCRM`. Requests
  never wait for the portal; without the admin module the document is built without the item after the 2 s. `GET configs/users/me` answers any
  caller without a token, LAN guests included, so nothing in this document may be a credential.
* `timestamp`: Monday 00:00 UTC of the current week (changes at most weekly, always younger than 30 days). `end_date`
  is omitted. Whether a changed timestamp marks the message new again is device check C1.
* With `adminlink` the marker is handled before the native call, so the page also opens when the native browser is not
  initialised offline (the opener's JNI fallback, §8.3). Without the patch the original body opens the marker URL itself
  (if the native browser is initialised): the view-only page (§2.4).
* When `crm.newsfeedLink` is false, `admin.enabled` is false or no `IAdminPortal` is registered, the document carries
  **`"newsfeeds": []`** (an absent key would leave an existing message in the inbox forever: `OnCrmDataSet` returns
  before `RemoveExpiredNewsFeed` when `NewsFeed` is null).
* A `newsfeeds` array from `crm.overlay` is kept; ours is added (replacing an entry with the same `news_id`) before the
  first kept entry with the same asset key (a missing asset is "", like ours), else at the end: the PC client gives
  every inbox message the dates of the first item of its asset (docs/design/pc-port.md §4.3).
* Implementation: `server/Services/Crm/CrmNewsfeed.cs` builds the list from `IAdminPortal.PageMarkerUrl` and the clock;
  `CrmDocumentBuilder.Build` gains a `List<object> newsfeeds` parameter applied after the `crm.overlay` merge and before
  `CrmDocumentValidator.Sanitize` (which gains a `newsfeeds` check, §10.8); `CrmDocumentCache` passes it and adds the
  week number to its cache key. `CrmOptions` gains `NewsfeedLink` (`crm.newsfeedLink`, default true).

### 9.2 Settings › Account "MCVS Offline" button (WP6: patch `adminpage`, default)

Flash `MenuAccountSettings.doAction` handles `customAction == "showRedeem"` itself and forwards every other
`customAction` to `Native.ProcessAccountMenuButtonAction(customAction)`; the list comes from
`Native.GetAccountMenuButtonList()`. Both natives are `PlayerProfileComponent` methods registered with
`UI.OverrideNativeFunction` (`PlayerProfileComponent.cs:2493-2495`), so they run on the game thread.

`patcher/Patches/AdminPagePatch.cs`:

```
Id "adminpage", AssemblyFile "Game.dll"
Description "Settings > Account: an 'MCVS Offline' button that opens the offline settings page (Game.dll)"
ProfileType           = "Game.PlayerProfileComponent"
ButtonListFingerprint = "5522a99141e39edccee31473340f733dbeaa8885316247f3349cd74d5238d5df"
ButtonActionFingerprint = "6bb9a563929381d3a3e60c77378b758aa63604a1ee6933551b752ac67564c053"
HooksType             = "McvsOffline.AdminHooks"
```

Locate and verify (before any change):

* `IEnumerable<object> GetAccountMenuButtonList()` (private, instance, no parameters; 93 instructions, one local
  `List<object>`): `RequireFingerprint(…, ButtonListFingerprint)`; exactly one `ret` (`IL_0150`), preceded by
  `ldloc.0` (`IL_014f`).
* `void ProcessAccountMenuButtonAction(string)` (private, instance; 25 instructions):
  `RequireFingerprint(…, ButtonActionFingerprint)`; `IL_0001` is `call Gameloft.Engine.World
  Gameloft.Engine.Component::get_World()`, `IL_0018` is `call bool System.String::op_Equality(string, string)`.
* `ImportServerMethod(module, context, HooksType, "AccountMenuButtons",
  "System.Collections.Generic.IEnumerable`1<System.Object>", "System.Collections.Generic.IEnumerable`1<System.Object>")`
  and `ImportServerMethod(…, "OpenPage", "System.Void", "System.String", "System.Object")`.

Change A1 — insert before the single `ret` of `GetAccountMenuButtonList`:

```
IL_014f: ldloc.0
         call IEnumerable<object> McvsOffline.AdminHooks::AccountMenuButtons(IEnumerable<object>)   // new
IL_0150: ret
```

Change A2 — prepend to `ProcessAccountMenuButtonAction` (branch target = the original first instruction):

```
         ldarg.1
         ldstr "mcvs_offline"
         call bool System.String::op_Equality(string, string)        // the operand of IL_0018
         brfalse ORIGINAL_IL_0000
         ldstr "account"
         ldarg.0
         call Gameloft.Engine.World Gameloft.Engine.Component::get_World()   // the operand of IL_0001
         call void McvsOffline.AdminHooks::OpenPage(string, object)
         ret
ORIGINAL_IL_0000: ldarg.0 ...                                       // unchanged original body
```

No IL exception handler: both hooks catch everything themselves (as `GameHooks.StoreItemException` does). Two report
notes. A second run on a patched assembly fails the fingerprint check ("is not the verified 1.17.32 code").
Registration (WP6): `new AdminPagePatch()` in `ApkPatcher.AllPatches` (after `SignaturesPatch`), `"adminpage"` in
`PatcherOptions.DefaultPatches` and `OptionalPatches`; `--help` and PLAYER-SETUP list it (WP8 docs).

**Build 13: only Redeem code and MCVS Offline, centred** (`hosts/embedded/Admin/AccountMenuLayout.cs`; setting
`admin.accountMenu.hideDeadButtons`, bool, default true, `live`). No new IL: both steps run in the A1 hook.

* *Filter.* With the server up and the setting on, `AccountMenuButtons` drops the entries with `snsId` 0 (Facebook) or
  2 (Google Play Games), whose login the `debloat` patch fails one frame later, and `customAction "restore_purchase"`
  (`IapStoreComponent.RestoreTransactions`: in-app purchases, not cloud progress; no store exists offline). The members
  are read by reflection from the game's anonymous objects; an entry that cannot be read, and anything else (Redeem,
  the PC's GameCenter `snsId 1`, the link-code entry of other platforms, unknown entries), stays. The Steam build's
  Win32 list is [Redeem, GameCenter] (no Facebook, Google or Restore entry, and `ShowLinkCode` only on platforms other
  than Android, iOS and Win32), so the setting changes nothing on PC and the Windows page does not show it. The filter runs before the
  `PortalEnabled` check (the MCVS entry is still appended only with the page on), and a list it would empty is returned
  as it was. Flash side effects: none beyond gameswf null-call lines the stock menu already logs
  (`removeButtonListeners` and `resultSNSConnect` look children up by `btnSocial_<k>` / `btnSocial_<sns>`).
* *Centring.* `MenuAccountSettings.onPush` places list key k at `x = 77 + floor(k/2)·(w+50)`,
  `y = 320 + (k%2)·(h+45)` (no centring, column-major, 2 rows), so [Redeem, MCVS Offline] would be stacked at the left
  edge. When the final list is 1 or 2 entries without `snsId` (Flash names them `btnSocial_customAction0/1`), the hook
  posts one work item to `GameApplication.Context`; `GameSynchronizationContext.Post` only enqueues, so it runs after
  `onPush`. It takes `UISceneExtentions.GetUI(World)`, `FindClass("gluic.components.menus", "MenusStack")
  .InvokeMethod("getMenuOnTopOfStack")` (named `menu_accounts`: `ASCharacter.Name`, else the AS `name`; not
  `MenusManager.getMenuByName`, which skips disposable menus), the two buttons through `getChildByName`, their `width`
  and the first one's `y`, and `FindClass("managers", "VPManager")` `getViewportBoundX/Width`. Then
  `x0 = vpX + (vpW − (w0 + 50 + w1))/2`, `x1 = x0 + w0 + 50`, the second button's `y` = the first's (one button:
  `x0 = vpX + (vpW − w0)/2`), set as `float` (an AS Number; `ASValue.SetObject` would wrap a `double` as a managed
  object). Both symbols are 841 wide with their registration point at the left edge, so on the S22 (viewport 2048 stage
  units at screen x 84-1916) the pair lands at x 158 and 1049, screen 225-1775, centred on 1000. One INFO line:
  `account menu: 2 buttons centred (vpX=0, vpW=2048, x0=158, x1=1049)`; any failure (no UI, another menu on top, a
  missing button or member, a row wider than the viewport, an exception) logs one `account menu: not centred (...)`
  line and moves nothing. Every `ASClass`/`ASCharacter`/`ASValue` wrapper is disposed. The members (`GetUI`,
  `MenuComponent.FindClass`, the non-generic `InvokeMethod(string, object[])`, `GetMember`, `SetMember`, `Name`,
  `ASValue.GetObject(Type)`) are in `GameReflection.Verify` (RealGameReflectionTests: Android and PC assemblies).
* *Open on a phone:* `vpW` in that log line should read 2048 on the S22 (layout research §2: if gameswf's stage width
  were the full screen in stage units, the pair would sit 84 px right of centre, and so would the game's own centred
  settings menus).

### 9.3 The inbox marker link (WP6: patch `adminlink`, default)

`patcher/Patches/AdminLinkPatch.cs`:

```
Id "adminlink", AssemblyFile "Gameloft.Engine.dll"
Description "the 'MCVS Offline' inbox message opens the offline settings page with a fresh one-time ticket (Gameloft.Engine.dll)"
BrowserType           = "Gameloft.Engine.BrowserComponent"
OpenUrlFingerprint    = "7585cdc7e72ffbe58c2a11dbc768aa94cfad044bea81a0330aaee6291db456cb"
HooksType             = "McvsOffline.AdminHooks"
```

Locate and verify: `void OpenUrl(string url, bool ingame)` (public, instance; 73 instructions; one exception handler,
the `youtube.com` try block `IL_0071`–`IL_00ac`); `RequireFingerprint(…, OpenUrlFingerprint)`; `IL_0000` is `ldarg.0`,
`IL_0001` `call Gameloft.Engine.World Gameloft.Engine.Component::get_World()`. `ImportServerMethod(module, context,
HooksType, "OpenIfPageMarker", "System.Boolean", "System.String", "System.Object")`.

Change L1 — prepend (outside the existing try block; branch target = the original first instruction):

```
         ldarg.1
         ldarg.0
         call bool McvsOffline.AdminHooks::OpenIfPageMarker(string, object)   // new
         brfalse ORIGINAL_IL_0000
         ret
ORIGINAL_IL_0000: ldarg.0 ...                                               // unchanged original body
```

Every caller funnels through this method: `UIComponent.OpenUrl` and `MenuComponent.NativeOpenUrl` (both register the
Flash native `OpenUrl`, `UIComponent.cs:405`, `MenuComponent.cs:330`; whichever wins), `MenuManager.Hyperlink`, the
dev-menu browser cheats and our own `PageOpener` (whose URL carries a ticket, so it passes through). Hooking the engine
method rather than `Game.UIComponent::OpenUrl(string, bool)` (15 instructions, fingerprint
`bee1818b1ee4aea9b06dc51e0ce18a900a40bf96c3127666c9a4b7908825001c`) covers both registrations. One report note. A
second run fails the fingerprint check. Registration (WP6): `new AdminLinkPatch()` in `ApkPatcher.AllPatches` (after
`DebloatEnginePatch`), `"adminlink"` in `DefaultPatches` and `OptionalPatches`.

### 9.4 Optional: Forum button (WP6: patch `forum`, not default)

Settings `btn_forum` → `Native.GoToForum` → `UIComponent.GoToForum()`, which calls `World.Browser.ShowForum()` (dead
offline). `patcher/Patches/ForumPatch.cs`: `Game.UIComponent::GoToForum()` (public, instance, 5 instructions),
`ForumFingerprint = "0b1e47e654f8198a7fef5095296c31c61693a5ecbe56eace9aee742789a93e8e"`; replace the body with

```
ldstr "forum"
ldarg.0
call Gameloft.Engine.World Gameloft.Engine.Component::get_World()   // the operand of IL_0001
call void McvsOffline.AdminHooks::OpenPage(string, object)
ret
```

Id `forum`, in `OptionalPatches` only. The label stays "Forum".

Build 19: with `admin.settingsMenu.trim` on, the Forum tile is hidden ([settings-menu.md](settings-menu.md) §1); on
Android with `admin.settingsMenu.performance` on it is the PERFORMANCE tile, whose `GoToForum` calls the game's own
`UIComponent.GoToForum()` (this patch's body) only while the tile is not active. So the patch is reachable only with the
trim off on PC, and with both switches off on Android.

### 9.5 Optional: hidden redeem code (WP7)

`AssetsService.Coupon`, before the `assets.couponsEnabled` check: if `admin.redeemCode` is non-empty and
`CouponCatalog.Normalize(code)` equals it, the request opens the page only when **all** hold: the request is from
loopback; `lanMode` is off (a LAN guest's redemption runs on this phone's menu server, `RedeemCouponRemote` with the
guest's `RemoteSource.Session`, and reaches the Federation over loopback with the game's `User-Agent` and no token, just
like the player's own); it looks like the game's own request (`GameRequest.NotTheGame`): no `Origin`, `Referer` or
`Sec-Fetch-*` header (a browser adds them; any web page on the phone could otherwise send this GET as an `<img>` or a
no-cors fetch), the client id in the path has the game's form `<digits>:<digits>:<version>:<platform>:<store>` (lower
case; the caller picks it, so `coupons/Linux/…` would match every Android browser's `(Linux; …)`), and the `User-Agent`
is exactly `[<agent> ](<client id>; <datacenter>)` for it, without `Mozilla/` (`OP/FedRequestMessage.cs:137-142`; a page
cannot set the `User-Agent` of a no-cors request, which also covers old WebViews without `Sec-Fetch-*`); an access token, **if
any**, resolves to the current player's account (`AdminApi.CurrentPlayer().AccountId`) — as built, the game's coupon
request carries **no** token (`AssetsApi` builds it without scopes and `FedRequestMessage` adds a token only to scoped
requests), so none is required; `IAdminHost.Describe().BrowserOpen` is false; the last honoured trigger is more than
30 s ago. Then `context.Features.Get<IAdminPortal>()?.OpenPage(AdminTriggers.Redeem, null)`. Either way the
answer is **404** `coupon does not exist` (nothing is claimed; with 200 the game would mark the code claimed and add an
empty inbox message) and the log says `coupon '<code>': opens the MCVS Offline page (requested|ignored: <reason>)`. The
game shows "code does not exist" behind the browser; acceptable for a power-user fallback. The request comes from the
menu server's HTTP client on a connection thread; `IAdminHost.OpenPage` posts to the game thread. A match cannot run
while the player is on the Redeem screen, so no further check is needed.

### 9.6 Not used

The dev cheat menu's Browser › ShowYoutubeVideo (developer check of the offline browser only); the social links list
(`GetSocialMenuButtonList`, would need another hook); `redir/*` Customer Care (needs `IsInGameBrowserInitialized`
and the native URL template; kept as the offline page).

## 10. Work packages

### 10.1 Ownership rules, integrator, staffing

* Every file below belongs to exactly one package; nobody else edits it. A package may add new files only inside the
  folders it owns or as files listed for it. Existing tests that a package's change breaks are fixed by that package, and
  they are listed in its file list. In `tests/McvsOffline.Tests/Admin/` ownership is **per file** as listed (the folder
  is shared by WP2, WP3, WP4 and WP8); shared helpers (`AdminTestClient.cs`, `FakeAdminHost.cs`) belong to WP2, and other
  packages use them without editing them (a needed change goes through WP2).
* **Integrator: WP8** (the lead). Owns the shared registration file `server/Services/ServiceRegistry.cs`, every doc
  outside this design, the end-to-end tests and the hook contract test. `patcher/ApkPatcher.cs` and
  `patcher/PatcherOptions.cs` are owned by WP6 (the only package that registers patches); `server/Core/McvsServer.cs`
  by WP1. Contract changes go through WP8, which updates §4/§8.1/§9 here.
* **Contract-first commits** (day 0, < 2 h each): WP2 commits `AdminContracts.cs` verbatim from §8.1, a compiling
  `AdminApi`/`GameSaveTracker` skeleton (signatures of §8.1, bodies may throw `NotImplementedException`) and
  `FakeAdminHost`; WP1 commits `LogRedaction.Redact`, `TokenService.LastDirectAnonymous`/`RevokeCredential` and the
  `LocalAccessGate.BodyLimit` signature. Every other dependency below is on these commits only, unless stated.
* Suggested staffing for three engineers: **A** = WP1 → WP2 → WP3 (server); **B** = WP4 → WP7 (page, CRM/redeem
  triggers); **C** = WP5 → WP6 (Android host, patcher). WP8 by A (or the lead) at the end.
* Merge order: WP1, WP2, (WP3, WP4, WP5, WP6, WP7 in any order), WP8.

### 10.2 WP1 — Core gate, HTTP limits and log hygiene (`server/Core`)

Files: `server/Core/McvsServer.cs` (gate call for every request in `Handle`; `ServerContext.Problems` sink created in
`Create` before settings load; logger redactor; header-time body limit wiring), `server/Core/ServerContext.cs`
(`public MemoryLogSink Problems { get; internal set; }`, `public Func<string, bool> IsValidAdminSession { get; set; }`,
`public Func<string, string, long> AdminRouteBodyLimit { get; set; }`), `server/Core/Http/LocalAccessGate.cs` (new),
`server/Core/Http/LogRedaction.cs` (new), `server/Core/Http/RequestLogger.cs`, `server/Core/Http/HttpRequestParser.cs`
and `server/Core/Http/HttpServer.cs` (a per-request body limit callback consulted after the headers and while reading a
chunked body), `server/Core/Logger.cs` (`Redactor`), `server/Core/CoreRoutes.cs` (`superPassword` → `***`),
`server/Core/Settings.cs` (`LoadError`, `MoveAsideAndReset(DateTime)`), `server/Core/Accounts/TokenService.cs`
(`LastDirectAnonymous`, `RevokeCredential(string)`), `server/Core/McvsConstants.cs` (`McvsVersion.Build`, string, bumped
per device build), `server/Services/Pandora/ServicePrefixAliases.cs` (`IsSpecific` false for `_mcvs`); tests
`tests/McvsOffline.Tests/Core/LocalAccessGateTests.cs`, `…/Core/FederationHostCheckTests.cs`,
`…/Core/EarlyBodyLimitTests.cs`, `…/Core/LogRedactionTests.cs`, `…/Core/LoggerRedactionTests.cs`,
`…/Core/SettingsRepairTests.cs`, `…/Core/TokenServiceAdminTests.cs`, `…/Core/PrefixAliasGateTests.cs` (new), and the
existing `tests/McvsOffline.Tests/Content/CrmServiceTests.cs`, `tests/McvsOffline.Tests/Unlocks/UnlockGranterTests.cs`,
`tests/McvsOffline.Tests/ServerIntegrationTests.cs`, `tests/McvsOffline.Tests/HttpServerTests.cs` (add `X-MCVS: 1` to
their `_mcvs` POSTs, adjust status assertions, Host values).

Details: §5.2, §5.4, §4.4 (`MoveAsideAndReset`), §6.6 (`LastDirectAnonymous`: set in both the new-grant and the
re-issued-grant branch of `Issue` when the caller passes `playerLogin: true` (as built: `auth/authorize` for the game's own
login only), under its lock, clone returned, with `ClientId` and `DeviceId`), §6.4
(`RevokeCredential`: removes every persisted grant of that credential, saves `tokens.json`, returns the count).
`Settings.LoadError` holds the read exception message or `MiniJson.Parse`'s exception message (line/column) when
`IsFallback`. `MoveAsideAndReset` is thread-safe (under `gate`), throws `InvalidOperationException` unless `IsFallback`
and backed by a file, uses `FileSystem.Rename`, then `Save()`. The gate reads `security.hostCheck`, `lanMode`, `bind`,
`publicUrl` and `admin.openVia` from the server settings per request (cheap getters).

Depends on: nothing.

Tests: unit — gate truth table (remote 127.0.0.1/::1/::ffff:127.0.0.2/192.168.1.5/null; Host `127.0.0.1`,
`127.0.0.1:51780`, `localhost:51780`, `[::1]:51780`, `127.0.0.1:51781`, `evil.com:51780`, empty, absent; methods;
`X-MCVS`; Origin `http://127.0.0.1:51780`, `null`, `http://evil.com`; `Sec-Fetch-Site cross-site`; `OPTIONS`); the
`POST /_mcvs/ui` exemption (form body, `Origin: null`, no `X-MCVS`, `Sec-Fetch-Site: none` → allowed only with
`admin.openVia=post`; with `cross-site`, a JSON body, a 2 KiB body or `openVia=auto` → refused); Federation Host rule
(loopback-bound: `evil.com` → 404 and one WARN per minute, `localhost:1234` and absent Host → allowed, `publicUrl` host
allowed; LAN mode, a `0.0.0.0` bind or `security.hostCheck=false` → allowed); header-time limits (a 2 KiB POST to
`/_mcvs/api/v1/settings` without a cookie → 413 before the body is read: the test feeds headers and checks the parser
never reads the body bytes; with a valid cookie → accepted up to 64 KiB; chunked bodies stop at the limit);
redaction of every pattern including percent-encoded, split across `&`/JSON, parameter names (`password=`,
`access_token=`, `"superPassword":`), access tokens anywhere in a line (`RedactsTheAccessTokenOfTheGamesSessionLog`,
`RedactsAccessTokensAnywhere`), the fast path returning the same instance (also for paths containing `mcvs`,
`PathsWithMcvsKeepTheFastPath`); `Logger.Redactor` applied to messages
and exception text for every sink; `Settings` fallback → `LoadError` set, `MoveAsideAndReset` renames, clears the flag,
saves defaults, refuses when not fallback; `LastDirectAnonymous` ignores on-behalf and `game:` grants and grants not marked
`playerLogin`, is updated on re-issue, carries client and device id; `RevokeCredential` removes grants of one credential only. HTTP through
`McvsServer.Handle` — `GET /_mcvs/status` from a LAN address → 404; with `Host: evil.com:51780` → 404; `POST
/_mcvs/crm/reload` without `X-MCVS` → 403, with → 200; `/storage/_mcvs/storage` → not the diagnostics route;
`/_mcvs/status` shows `superPassword: "***"`; an `auth/authorize` form with `password=` is logged redacted (MemoryLogSink
at DEBUG); a `/_mcvs/api/...` request that throws in its handler is logged without body; a Federation request
(`GET /config/...`) with `Host: 127.0.0.1:51780` is unaffected.

### 10.3 WP2 — Admin framework, settings, status, actions, exit sequence, page serving (`server/Services/Admin`)

Files (new): `server/Services/Admin/AdminContracts.cs` (verbatim §8.1), `AdminApi.cs`, `AdminService.cs` (module
`admin`: `Register(Router, ServerContext)`), `AdminSecurity.cs` (tickets, sessions, cookie, rate limiter, the
`IsValidAdminSession` and `AdminRouteBodyLimit` delegates), `AdminPortal.cs`, `AdminJson.cs`, `CurrentPlayerResolver.cs`,
`GameSaveTracker.cs`, `ExitSequence.cs`, `SettingsSchema.cs` (§3), `SettingsApi.cs`, `StatusApi.cs`, `ActionsApi.cs`,
`PageRoutes.cs`; changed: `server/Services/Assets/CouponCatalog.cs` (`Describe()`: code, description, numeric-suffix
flag); tests `tests/McvsOffline.Tests/Admin/AdminTestClient.cs` (in-process client over `McvsServer.Handle`: sets
`RemoteEndPoint = 127.0.0.1:40000`, `Host: 127.0.0.1:51780`, `Origin`, `X-MCVS`, JSON bodies, a cookie jar,
`MintTicket(trigger)` through `IAdminPortal`), `…/Admin/FakeAdminHost.cs` (records every call in order; configurable
results), `AdminSecurityTests.cs`, `SettingsApiTests.cs`, `SettingsSchemaTests.cs`, `StatusApiTests.cs`,
`ActionsApiTests.cs`, `ExitSequenceTests.cs`, `CurrentPlayerTests.cs`, `PostModeTests.cs`, and the existing
`tests/McvsOffline.Tests/Content/AssetsTests.cs` if `Describe()` changes what it asserts.

Details: §4.2–§4.5, §5.3, §6.6, §8.8. `AdminService.Register`: create `AdminSecurity`, `AdminApi`, `AdminPortal`,
`GameSaveTracker` (subscribes to `storage.profile.changed`), `ExitSequence`; `Features.Set(api)`,
`Features.Set<IAdminPortal>(portal)`; set the two `ServerContext` delegates; snapshot settings for `restartPending`;
record `settings.json` length/mtime; register page, session, ping, status, settings, codes, clipboard and action routes
and the `_mcvs/api/{*rest}` catch-all. The page bytes come from `BuiltInContent.All["admin/index.html"]` (500
`internal` "page missing from this build" when absent, so WP2 does not depend on WP4). Settings backups go through
`AdminApi.CreateBackup` (set by WP3; when null the response carries a warning), so WP2 does not depend on WP3. Tests
register the module with `ServerOptions.RegisterServices = (r, c) => { ServiceRegistry.RegisterAll(r, c);
AdminService.Register(r, c); }` until WP8 adds the registry line.

Depends on: WP1 (gate, redaction, `Settings.LoadError/MoveAsideAndReset`, `LastDirectAnonymous`,
`ServerContext.Problems` and delegates).

Tests: unit — schema: every key in §3.1 has label/help/type/default/apply, defaults equal the module defaults
(`Settings.CreateDefaults`, `StorageSettings`, `LiveOpsSettings.Defaults()`, `CrmOptions`), the denylist is never
editable; validation of each type (bounds, NaN, wrong JSON type, enum, pattern, tokens); ticket lifetime/one-use/
eviction/constant-time compare; a valid ticket succeeds while failures are rate-limited; session idle/absolute expiry;
rate limiter. `ExitSequence` with `FakeAdminHost` and a fake clock: save-now order (toast, close, wait loop, request
save, wait, reopen with `s=saves`, `r=nosave` on timeout); restart order (… wait, lock, flush, `Exit`); no save +
`restartWithoutSave=false` → no `Exit`, `exit.last = cancelled`; `Exit` false → lock released, flag cleared; a second
request while running → 409. `GameSaveTracker` ignores `admin` saves, honours `sinceUtc` and the quiet period. HTTP via
`McvsServer.Handle` — page served with CSP and `text/html`; session exchange with a valid, used, expired and unknown ticket
(401 each, no difference in body), cookie attributes exact; post mode: form POST with `Origin: null` and no `X-MCVS` →
303 with cookie and `Location`; repeat with the used ticket and the cookie → 303 without a new session; every API route
without cookie → 401; writes without JSON → 415, without `X-MCVS` → 403, too large → 413; `POST settings` writes
`settings.json` (read back from disk), all-or-nothing on one bad key, `not_editable` for
`port`/`superPassword`/`crm.overlay`/`security.hostCheck`/an unknown key, `file_changed` after an external edit, `force`
writes `settings.json.bak-*` first; enabling `devMenu` calls the backup delegate once with `presettings` and returns its
id; fallback file → GET shows `fallback`/`error`, `settings/file` masks `superPassword`, POST → 409, repair moves the file
and a later POST works; `settings/file` → 404 when not fallback; `restartPending` after changing `devMenu`; status with
and without `?storage=1`; `player.source` for each resolution path (`game` from the fake host's credential, `session`
through `auth/authorize` with matching and with foreign device id, device, single); `problems` contains a WARN logged by
another module and never a ticket; restart → 501 without host; `ping` calls `PageAlive` only with a session;
`admin.enabled=false` → 503 and `IAdminPortal.Enabled` false; `IsPageMarker` exact.

### 10.4 WP3 — Saves, transfer text, staged operations, probation, logs (`server/Services/Admin/Saves`, `…/Logs`, storage backups)

Files (new): `server/Services/Admin/Saves/SavesService.cs` (module `admin-saves`), `SaveBundle.cs`, `SaveTransfer.cs`,
`ImportUploads.cs`, `StagedOperations.cs`, `Probation.cs`, `server/Services/Admin/Logs/LogsService.cs` (module
`admin-logs`), `LogTail.cs`, `LogsZip.cs`, `ZipWriter.cs`; changed: `server/Services/Storage/LocalProfileStore.cs`
(`Backup` semantics, `CreateBackup`, `DeleteBackup`, `ReadBackupText`), `server/Services/Storage/ProfileBackups.cs`
(pools, `PoolOf`, `Create` with `autoKeep`, per-pool `Prune`, `BackupInfo.Pool/Deletable`,
`ManualBackupLimitException`); tests `tests/McvsOffline.Tests/Admin/SavesApiTests.cs`, `SaveBundleTests.cs`,
`SaveTransferTests.cs`, `ImportUploadsTests.cs`, `StagedOperationsTests.cs`, `ProbationTests.cs`, `LogsApiTests.cs`,
`LogTailTests.cs`, `ZipWriterTests.cs`, `tests/McvsOffline.Tests/Storage/BackupPoolsTests.cs` (new), and the existing
`tests/McvsOffline.Tests/Storage/ProfileStoreTests.cs` (backup-count and keep-0 assertions).

Details: §4.6, §4.7, §6, §7.1–§7.4. Both modules get `AdminApi` from `context.Features`; when it is null they register
no routes, but `admin-saves` still applies staged operations and runs probation. `SavesService.Register` sets
`api.CreateBackup` and subscribes to `storage.profile.changed` (probation). Folder layout
`<data>/admin/{staged,exports,share,uploads}/` and `<data>/import/` (created on demand).

Depends on: WP2 (AdminApi, CurrentPlayer, AdminTestClient, FakeAdminHost), WP1 (LogRedaction, `RevokeCredential`).

Tests: unit — bundle round trip keeps the profile file byte-for-byte (verbatim `_mcvs.gameProfile` with odd
spacing/escapes) and CRC; each §6.1 error (no marker, `{}`, a settings.json, no game profile, account mismatch,
upper-case id, version 2, unknown transport) gives `canStage: false`; raw profile file accepted; transfer text: round
trip for methods `d` and `s` at part sizes 60000/3900/1900; parts pasted in any order, several per paste, surrounded by
chat noise, with wrapped lines and CRLF; a damaged part is rejected by number; a part of another set is rejected; an
inflate bomb (> 16 MiB) → 413; output decodes with Python-compatible raw deflate (a fixture produced by `zlib` with
`wbits=-15`); `CanDeflate` false → method `s`; backups: `keep=0` + `CreateBackup` keeps existing `auto` backups; an apply
with `keep=0` still writes its `preimport` backup; 20 `auto` + 25 `event` + 12 `system` → caps per pool; an 11th manual
→ `ManualBackupLimitException`, nothing written; `Delete` only for manual; pool detection on ids produced by `Create`
(`…-preimport`); `LogTail` on both formats, multi-line exceptions, a window cut mid-line, level/substring filters;
`ZipWriter` output opened by `System.IO.Compression.ZipArchive` on net8 and by `unzip -t` when present, CRCs correct, no
data-descriptor flag, method 8 entries; scope `recent` stays ≤ 8 MiB with oversized inputs (skipped entries listed).
HTTP via `McvsServer.Handle` — list/backup/delete/restore/relink/export (attachment headers, `.txt` content), paste and
validate for each source kind and plan, `newer_save`, `staged_exists`, `current_player_unknown`, `manual_full`,
unstage, undo (`nothing_to_undo` before any apply); **boot apply**: create server A, authorize an anonymous credential
C1 (account X) and save a game profile, export as transfer text; create a fresh data dir with server B, authorize C2
(account Y), paste the parts, stage the import, dispose, `McvsServer.Create` again on the same dir → before `Start`:
profile of X equals the bundle text's document (`ReadDocument` + view compare), messages copied (originals in
`history/<id>/messages/`), `Accounts.Find("anonymous:C2")` is X, grants of C2 revoked, the stage-time `preimport` backup
reused when the file did not change, history `applied` with `done` and `durationMs`, `pending.json` gone,
`probation.json` present; an `http` save of X clears probation; without it, two more `Create`s revert (X's pre-backup
restored, C2 back on Y, history `reverted`); a crash simulated after step 5 (exception injected in step 6) resumes at
step 6 on the next `Create` without a second `preimport` backup beyond the documented case; a corrupt payload → history
`failed`, no relink, nothing written; `attempts > 3` gives up; relink owner mismatch → profile applied, relink skipped
with a warning; undo of a switch relinks back. Logs: list both dirs (fake host `FallbackLogsDir`), tail with filters,
zip then share with a fake host (zip on disk, `ShareFile` called with `application/zip`), `busy` on concurrent zip
builds, share-text 501 returns `text`, names with `..`/`/` → 404.

### 10.5 WP4 — The page (`server/Content/admin`)

Files: `server/Content/admin/index.html` (new), `server/Content/README.md` (one table row: hand-written, not by
content-gen), tests `tests/McvsOffline.Tests/Admin/PageSmokeTests.cs`, `tests/McvsOffline.Tests/Admin/page-smoke.js`
(new).

Details: §2 (geometry, screens, ids, banners, view mode, boot sequence, Back guard), §2.3 (rules), §3 (render from
schema), §4 (API), §6.2 (part sizes and counts shown before copying). The page keeps no key lists; unknown `type` values
render read-only.

Depends on: none to start (the API is fixed here; develop against `page-smoke.js` fixtures); the live smoke run needs
WP2 and WP3.

Tests: `PageSmokeTests` (always): exactly one inline script and style, no external URL, no `localhost`, ≤ 200 KB, all
ids of §2 present, ES5 lint (tokenizer that skips strings/comments/regex literals; fails on template literals, the
banned keywords/punctuators/identifiers of §2.3, `for (… of`), CSS lint (§2.3: also no `vh`, no fixed `height` on text
rows). Node smoke (when `node` is on PATH; required with `MCVS_REQUIRE_NODE=1`): the C# test starts a real `McvsServer`
on port 0 with a temp data dir (all modules incl. admin), mints a ticket, runs `node page-smoke.js
<url-with-fragment>`; the script loads the HTML, runs the inline script in `vm` with a minimal DOM (exactly the surface of
§2.3, including `history.pushState`/`popstate` and `visibilitychange`) and a cookie-aware XHR over `http`, then: clicks
every nav button, toggles `#set-instantBots`, presses `#settings-save`, opens Logs and Saves, exports as text and pastes
the parts back (`#import-add` twice), and checks the Back guard (a pending setting edit pushes one history entry; a
simulated `popstate` opens the discard dialog). A second run boots with an already used ticket and a valid cookie (the
activity re-creation case) and must not show `#banner-nosession`; a third boots with `#news` and no cookie and must show
`#banner-viewmode`. It exits 0 when every screen rendered without a script error or `#banner-error`; the C# side then
checks `settings.json` changed. If `MCVS_ACORN` points to an `acorn.js`, the script also parses the page with
`ecmaVersion: 5`.

### 10.6 WP5 — Android host integration (`hosts/embedded`)

Files: new `hosts/embedded/Admin/AdminHost.cs`, `hosts/embedded/Admin/AdminHooks.cs`, `hosts/embedded/Admin/PageOpener.cs`,
`hosts/embedded/Admin/GameReflection.cs`, `hosts/embedded/Android/AndroidShare.cs`, `hosts/embedded/Android/AndroidExit.cs`,
`hosts/embedded/Android/AndroidJni.cs`, `hosts/embedded/Android/MediaStoreLedger.cs`; changed
`hosts/embedded/IEmbeddedPlatform.cs` (`IAdminPlatform`), `hosts/embedded/EmbeddedHost.cs` (wiring, problem sink,
logger redactor, JNI table, restart marker), `hosts/embedded/Android/AndroidPlatform.cs` (implements `IAdminPlatform`),
`hosts/embedded/Android/PublicMirror.cs` (§7.5), `hosts/embedded/Android/GameLogBridge.cs` (redaction),
`hosts/embedded/Diagnostics/CrashReporter.cs` (redaction), `hosts/embedded/Diagnostics/GameThreadWatchdog.cs`
(`PauseFor`); tests `tests/McvsOffline.Embed.Tests/Embedded/AdminHostTests.cs`, `…/Embedded/AdminHooksTests.cs`,
`…/Embedded/WatchdogPauseTests.cs`, `…/Embedded/JniTableTests.cs`, `…/Embedded/RealGameReflectionTests.cs`,
`…/Embedded/ExitPathTests.cs` (new), and the existing `…/Embedded/DiagnosticsTests.cs` (mirror and crash-copy changes).

Details: §8 (all), §7.5, §7.6. Android-only code behind `#if MCVS_EMBEDDED`; the logic (lease, capability flags,
opener selection by `admin.openVia`, exit path selection by `admin.restartVia` and its fallback, button list building,
marker matching, reflection over `World.Browser`/`Authentication`/`PlayerProfileComponent`, the ledger's pruning) is
platform-independent and testable on net8 with fakes.

Depends on: WP2 contract commit (`AdminContracts.cs`), WP1 (`LogRedaction`, `Logger.Redactor`). Not on WP6 (the hook
signatures are fixed in §8.1).

Tests: unit (net8, Embed.Tests) — `AccountMenuButtons`: appends exactly one dictionary with the four keys and values,
returns a `List<object>`, keeps the input order, handles null and non-list input, unchanged when the portal is disabled
or absent, never throws; `OpenPage`: with a fake world/browser object (properties `Browser`,
`IsInGameBrowserInitialized`, method `OpenUrl(string, bool)`) calls `OpenUrl(url, true)` with
`http://127.0.0.1:<port>/_mcvs/ui/#t=` + 32 hex; not initialized → the fake `IAdminPlatform.OpenInGameBrowserJni`; `jni`
and `post` modes (post: `SetParameters` then post data `t=<ticket>`, URL without fragment); null world → posts through a
`FakeGameApplication` context (as in `DiagnosticsTests`); `OpenIfPageMarker`: the marker → `OpenPage("news")` and true;
any other URL, a disabled portal, a server World's component, a throwing world → no open, correct return value, no
exception; watchdog: with `PauseFor` active and no beats there is no STALL report and no dump, after expiry the normal
5 s report comes, one `paused`/`resumed` line each, `PauseFor(0)` ends it; `AdminHost.Describe` on `DirectoryPlatform`
has every capability false and `ShareFile` returns `none`; `PageAlive` extends the lease; `Exit` with a recording fake
platform: `game` → [`IsInGameBrowserOpen`, `ExitViaGame(true)`], browser still open → `QuitInGameBrowser` first, a
`game` path that "survives" 3 s → `ExitViaHost(true)`; `none` → false and no marker written; the ledger keeps the newest 5
per kind and drops missing rows; host `RecentProblems` returns host WARN lines (not the watchdog's `paused`/`resumed`,
which are INFO); crash files are redacted; `UiCall`: a call the UI thread did not start within the wait is withdrawn and
never runs, also when the UI thread takes it just as the wait ends, and a `game` restart withdrawn that way is followed by
exactly one `ExitViaHost`, while one already running is not; the mirror replaces each copy with a new file (a hard link
to the old copy keeps the old text) and copies a dump or ANR trace again while it grows.
`RealGameReflectionTests` (MCVS_REFS): every member `GameReflection` resolves exists with the expected signature in the
real `Gameloft.Engine.dll`/`Game.dll` (`GameApplication.Instance/World/Context`, `World.Browser`, `World.Authentication`,
`AuthenticationComponent.AnonymousCredential` and its string conversion, `World.EnumerateActiveComponents(Type,
ComponentGroupOptions)`, `PlayerProfileComponent.ForceSaveProfile(CancellationToken)` returning `Task`).
`JniTableTests` (§8.6). Embedded build — `scripts/build.sh` with `MCVS_REFS` (`EmbeddedDllResolutionTests` with
`MCVS_REQUIRE_EMBEDDED=1` covers `DeflateStream` and every JNI-related managed member). Device — §12.

### 10.7 WP6 — Patcher triggers and the manifest invariant (`patcher`)

Files: new `patcher/Patches/AdminPagePatch.cs`, `patcher/Patches/AdminLinkPatch.cs`, `patcher/Patches/ForumPatch.cs`;
changed `patcher/ApkPatcher.cs` (`AllPatches`), `patcher/PatcherOptions.cs` (`DefaultPatches` + `"adminpage"`,
`"adminlink"`; `OptionalPatches` + `"adminpage"`, `"adminlink"`, `"forum"`), `patcher/Program.cs` (help text, if it lists
ids); tests `tests/McvsOffline.Embed.Tests/Patcher/RealAdminPagePatchTests.cs`,
`tests/McvsOffline.Embed.Tests/Patcher/RealAdminLinkPatchTests.cs`, `tests/McvsOffline.Embed.Tests/Patcher/ManifestTargetSdkTests.cs`
(new; their stand-in server source defines `McvsOffline.AdminHooks` with recording statics, not
`RealGamePatchTests.HookServerSource`), `tests/McvsOffline.Embed.Tests/Patcher/RealGamePatchTests.cs` (add the two new
patches to the default patch lists it applies together).

Details: §9.2, §9.3, §9.4, I1'.

Depends on: nothing (the hook signatures are in §8.1).

Tests: real `Game.dll`/`Gameloft.Engine.dll` (MCVS_REFS; skipped without, required with `MCVS_REQUIRE_REAL_CLIENT=1`) —
the shipped methods have the four fingerprints of §9; `adminpage` produces exactly two notes and the IL of §9.2 (Cecil:
last three instructions of the button list are `ldloc.0; call AdminHooks::AccountMenuButtons; ret`; the first nine of
the action method as listed; the rest unchanged — compare instruction strings after the prefix with the original);
`ProcessAccountMenuButtonAction` executed on net8 in a `GameContext` with an uninitialized `PlayerProfileComponent` whose
`GameObject.world` is set (as in `RealGamePatchTests`): action `"mcvs_offline"` → the stand-in hook recorded
`("account", world)` and the original body did not run; actions `"restore_purchase"` and `"x"` → hook not called, and
the outcome (return or exception type) equals the unpatched assembly's (differential); `adminlink`: one note, the first
five instructions as in §9.3, the original body and its exception handler unchanged (offsets shifted only), executing
`BrowserComponent.OpenUrl` on an uninitialized instance with the marker URL → the stand-in recorded the call and the
original body did not run (no native call attempted), with another URL and the stand-in returning false → the
differential outcome equals the unpatched assembly's; `ForumPatch`: fingerprint, body replaced, executing `GoToForum`
calls the hook with `"forum"`; every patched method JITs (`RuntimeHelpers.PrepareMethod`); ILVerify clean when `ilverify`
is installed (as `RealDebloatPatchTests`); a second patch run throws `PatchException`; all default patches still apply
together; `--dry-run` on the real APK lists `adminpage` and `adminlink`. `ManifestTargetSdkTests` (`TestEnvironment.GameApk`)
— as built, it asserts I1' instead of I1: the default target 35 (with the `classes.dex` edits of the same run, build 10),
`minSdkVersion` 21, `exported`, `POST_NOTIFICATIONS`, `<queries>` and the browser layouts at 35 and 36, `--target-sdk 29`
equal to the output without the patch, and 31+ refused without the dex edits (`ATarget31OrMoreNeedsTheDexEditsOfTheSameRun`).

### 10.8 WP7 — Server-side triggers: CRM inbox item, redeem code (`server/Services/Crm`, `…/Assets`)

Files: new `server/Services/Crm/CrmNewsfeed.cs`; changed `server/Services/Crm/CrmDocumentBuilder.cs`,
`server/Services/Crm/CrmDocumentCache.cs`, `server/Services/Crm/CrmOptions.cs`, `server/Services/Crm/CrmDocumentValidator.cs`
(`Sanitize` today ignores `newsfeeds`; it gains a check: a non-array becomes `[]`, non-object entries are removed,
`news_id`/`title`/`body`/`redirect`/`asset`/`youtube` become strings, `timestamp` an integer, entries without
`news_id` are removed, each removal logged like the pointcut checks), `server/Services/Assets/AssetsService.cs`; tests
`tests/McvsOffline.Tests/Content/CrmNewsfeedTests.cs`, `…/Content/RedeemPageCodeTests.cs`,
`…/Content/RealClientNewsfeedTests.cs` (new; `Misc/ClientConformanceTests.cs` is not edited).

Details: §9.1, §9.5.

Depends on: WP2 contract commit (`IAdminPortal`, `AdminTriggers`; for the redeem checks `AdminApi.CurrentPlayer` and
`IAdminHost.Describe`).

Tests: unit/HTTP via `McvsServer.Handle` (with a fake `IAdminPortal` and fake `IAdminHost` set in `RegisterServices`)
— the document has the item with the exact keys and types, `redirect` equals the portal's marker and contains no `t=`,
the timestamp is Monday 00:00 UTC of the current week (fake clock across a week boundary: the cache rebuilds);
`crm.newsfeedLink=false`, a disabled portal and no portal give `"newsfeeds": []`; an overlay `newsfeeds` entry is kept and
one with our `news_id` replaced; toggling the setting rebuilds the cached document; `/_mcvs/crm` diagnostics still work.
Redeem: `GET coupons/<cid>/mcvsoffline` as the game sends it (no token, its `User-Agent`), and with the current player's
token → 404 and `OpenPage("redeem")` called once, also when `assets.couponsEnabled=false`; the real client's redemption
opens it too; a second request within 30 s, a token of another account, an `Origin`/`Referer`/`Sec-Fetch-*` header,
another `User-Agent` (also `Mozilla/5.0 (<client id>; mcvs)` and an old WebView's against the client id `Linux`), a
client id not of the game's form, `lanMode` on, a non-loopback caller or `BrowserOpen=true` → 404 without a call (logged `ignored: <reason>`); empty `admin.redeemCode` →
normal 404 without a call; other codes unchanged. Real client (MCVS_REFS) — the real `Gameloft.OnlinePlatform.dll`
`NewsFeedData` built from our `newsfeeds` token (through its Newtonsoft from MCVS_REFS) gives one item with our id,
title, body, redirect and `CreationDate` = timestamp; the real `Game.dll` `new InboxMessage(NewsFeedItem, DateTime)` gives
`Id == "newsfeed_mcvsoffline_FromMCVS"`, `From == "MCVS"`, `RedirectLink == redirect`; the whole document still parses in
the real `StandardCrm`.

### 10.9 WP8 — Integration and documentation (integrator)

Files: `server/Services/ServiceRegistry.cs` (after `unlocks`: `Register("admin", Admin.AdminService.Register, …)`,
`Register("admin-logs", Admin.Logs.LogsService.Register, …)`, `Register("admin-saves", Admin.Saves.SavesService.Register,
…)` — `admin-saves` last), `hosts/standalone/Program.cs` (console URL, §8.10), docs `docs/STATUS.md`,
`docs/PLAYER-SETUP.md` (page, triggers, which import path works on which Android version (§6.8), transfer text, share,
save now and restart, Chrome unsupported, `adminlink`), `docs/PROTOCOL.md` (endpoint map, the Host check on every
route), `docs/MODULES.md` (admin modules, features `IAdminHost`/`IAdminPortal`/`AdminApi`, gate rules),
`docs/DEV-LOOP.md` (`-H 'X-MCVS: 1'`, Host check, `security.hostCheck`), `docs/ARCHITECTURE.md` (patch list, I1'),
`docs/ROADMAP.md`, `docs/protocol/content.md` (newsfeeds marker, redeem code), `docs/protocol/storage.md` (backup pools,
`keep` semantics, staged operations, probation), `docs/design/settings-page.md` (as-built notes); tests
`tests/McvsOffline.Tests/Admin/AdminEndToEndTests.cs`, `tests/McvsOffline.Embed.Tests/Embedded/AdminHooksContractTests.cs`
(new).

Depends on: WP1–WP7.

Tests: end-to-end over a real socket (`McvsServer.Start` on port 0, `RawHttpClient`): ticket → cookie → settings write →
CRM document reflects the change; legacy `_mcvs` routes reject a foreign Host; a Federation route rejects `Host:
evil.com` and accepts `127.0.0.1:<port>`; a 2 KiB unauthenticated POST under `/_mcvs/` is refused with 413 before the
body is sent (`Expect: 100-continue` gets no 100); stage an import, `Dispose`, create and start a new server on the same
data dir, authorize the relinked credential through `auth/authorize` and get the imported account; then an `http` save
clears probation. Contract: `typeof(McvsOffline.AdminHooks)` (compiled into Embed.Tests) has the three public static
methods with exactly the signatures `AdminPagePatch` and `AdminLinkPatch` import, and, when
`TestEnvironment.EmbeddedServerDll` exists, Cecil finds them in the real embedded DLL (`ApkPatcher.PatchAssembly` with both
patches succeeds against it). Full suite (`scripts/build.sh` with `MCVS_REFS`, `MCVS_REQUIRE_EMBEDDED=1`,
`MCVS_REQUIRE_REAL_CLIENT=1`), APK build with the same key, `--dry-run`, then the device test of §12.

As built: `AdminEndToEndTests` (servers composed by `ServiceRegistry` alone, raw HTTP/1.1 on a free port) and
`AdminHooksContractTests` (the hooks compiled from `hosts/embedded` are used as the server of the real `adminpage`,
`adminlink` and `forum` patches, whose imports must be exactly the three methods; then the same against the embedded
DLL). The temporary pass-through `AdminHooks` of the patcher tests (`MCVS_ADMINHOOKS_STAND_IN`) is gone. The page
smoke against a live server registers through `ServiceRegistry` and, with `MCVS_REQUIRE_NODE=1`, never skips. Loose ends
closed at the integration: post mode's `s`/`r` (§5.3), the masked revoke log line (§6.4), the page showing game.log's
own clock as it is, offering larger parts or a file after an export 413 and reading a not-ok text share right, and the
303/415/429 reason phrases in `HttpStatus`.

## 11. Test plan summary

1. **Unit** (net8, `tests/McvsOffline.Tests`, `tests/McvsOffline.Embed.Tests`): gate (incl. the post exemption and the
   Federation Host rule), header-time limits, redaction in every logger, settings repair, schema/validation,
   tickets/sessions, current player, exit sequence order, bundle and strict validation, transfer text, backup pools,
   staged apply with recorded steps, probation and revert, tail, zip, hooks, marker matching, watchdog lease, exit paths,
   JNI table.
2. **HTTP integration through `McvsServer.Handle`** with `AdminTestClient` (loopback `RemoteEndPoint`, real Host/
   Origin/headers/cookies): every route of §4.2, every error code of §4.1, the legacy `_mcvs` routes, the staged boot
   apply (create → stage → dispose → create), probation over three `Create`s.
3. **Real client / real assemblies** (MCVS_REFS): newsfeed parsing (`NewsFeedData`, `InboxMessage`, `StandardCrm`),
   the `adminpage`/`adminlink`/`forum` IL (fingerprints, executed hooks, differential, JIT, ILVerify), the game members
   `GameReflection` uses, the JNI table against `android.jar` and the APK's dex, the target SDK after patching (I1').
4. **Page**: static checks + ES5/CSS lint always; node smoke against a live in-process server when node exists (incl.
   the re-creation boot, view mode, transfer round trip and Back guard).
5. **Embedded build**: `scripts/build.sh` with the game's assemblies; `EmbeddedAssemblyTests`,
   `EmbeddedDllResolutionTests`.
6. **Device**: §12.

## 12. Device-test checklist (next device test, build 9)

Record `adb logcat -s McvsOffline mono-stdout`, keep `server.log`, and screenshot each step.

| # | Check | Expected / what to record |
|---|---|---|
| C1 | Inbox: a message "MCVS Offline" from "MCVS" appears after reaching the menu. | Button style and text for a link-only message; expiry text; whether it can be pinned/deleted; after a restart and after a CRM refresh (return from a match): is it flagged new again? (Also after the weekly timestamp change, if the test spans a Monday.) |
| C2 | Tap its button (build with `adminlink`). | The in-game browser opens `http://127.0.0.1:51780/_mcvs/ui/` (cleartext to 127.0.0.1 works); `server.log`: `admin: page opened (trigger news, via …)` and `admin: session opened (trigger news, <User-Agent>)`. Record the WebView version (About screen). If a build without `adminlink` is at hand: the view-only page with `#banner-viewmode`. |
| C3 | Fragment, cookie and the fallbacks. | The session works on the first load (the ticket survived the native `OpenURL` path) and after the toolbar Refresh (cookie kept). **Then deliberately** set `admin.openVia` to `jni`, restart, open, use, close, open again; the same with `post` (303 then the page; Refresh keeps working; "Save the game now" reopens on Saves, with "The game did not save" when it did not). For each: no native crash on close, the second open works (the `k` flag was reset), game sound state afterwards. Record in STATUS. |
| C4 | `IsInGameBrowserInitialized` offline. | Status › host › browser = `initialized`; `server.log` shows `marketing_site/redir/ingamenews.php?action=checkNews` or not. |
| C5 | Watchdog. | No `[watchdog] STALL` and no thread dump while the page is open (≥ 2 min), `[watchdog] paused`/`resumed` lines; time from Close to a responsive menu (GL context resume). |
| C6 | Settings › Account. | The "MCVS Offline" button shows with the typed label (or a raw key?) and a sensible icon (`Settings.png`; alternatives `settings_gameplay.png`, `redeemCode.png`); tapping opens the page (`trigger account`). Facebook/Google/Redeem/Recover buttons unchanged. Status › player › source = `game`. |
| C7 | Redeem code `MCVSOFFLINE`. | The page opens; the "code does not exist" popup behind it; nothing claimed; a second try within 30 s is ignored (logged). `server.log` shows `coupon 'MCVSOFFLINE': opens the MCVS Offline page (requested)` for the game's request (no token, the game's User-Agent); if it says `ignored: not the game's User-Agent` instead, record the request's `User-Agent` (DEBUG request line). The game's own login must not log `not taken as the player's login`. |
| C8 | Forum (if patched with `--patch default,forum`). | The page opens. |
| C9 | Settings writes. | VersusCoins ×2: after the next match the win pays double; a `restart` key shows the banner; Restart applies it. Enabling `devMenu` creates a `presettings` backup (Saves). Edit `settings.json` to a syntax error, start: the repair panel (file text with `superPassword` masked); repair; restart: defaults plus host keys. |
| C10 | Copy. | Which of `navigator.clipboard`, `execCommand`, host clipboard worked (shown in the toast); a 60,000-character part copies and pastes intact. |
| C11 | Saves. | (a) Save the game now: the page closes, `server.log` shows the TS dump POST, the page reopens on Saves with a fresh "last saved" time. (b) Back up now, then delete that backup. (c) Export → Share as a file to Gmail, Telegram, Discord, Drive: the `.txt` arrives with its real size. (d) With an **unlock-all profile (~1.4 MB)**: record the transfer text size and the part counts for 60,000/3,900/1,900. (e) On a second phone (or after clear-data): open the `.txt`, copy and paste the parts (record which viewer allows selecting and copying a 60,000-character part), stage (`switch`), restart: the game shows the imported progress; Status shows the history `applied`; probation clears within seconds of reaching the menu; the previous account is listed with "Play this account on this phone"; Undo goes back. (f) Import from `McvsOffline/import/` with `adb push`. (g) **Samsung My Files on the S22 (Android 16)**: can it copy a file into `Android/data/com.gameloft.android.ANMP.GloftMVHM/files/McvsOffline/import/`? (h) `server.log`: apply duration vs the first Federation request after the port opened. (i) Stay 2 min in the share target app, come back, play a match: no STALL dump, the game works. |
| C12 | Restart. | From Actions: toast, browser closes, the TS save in `server.log`, the process ends, and does it relaunch by itself? Count processes and boots per restart (`adb shell pidof com.gameloft.android.ANMP.GloftMVHM` before/after, the number of `[boot] MCVS Offline host started` lines, `ApplyPending` attempts with a staged import). Next boot logs `previous exit: restart requested`. Repeat with `admin.restartVia=host`. Decide the default from the results. |
| C13 | Logs share. | Build (scope Recent): the size shown, ≤ 8 MiB; the zip arrives non-empty in Gmail, Telegram and Discord; scope Everything: size and the page's warning. Files › Downloads › MCVS-Offline shows correct sizes for the current session's mirror files; `adb shell content query --uri content://media/external/file --projection _data:_size:is_pending --where "_data LIKE '%MCVS-Offline%'"` shows `is_pending=0` and real sizes (before the fix: `is_pending=1`, size 0/NULL); after 6 shares only the newest 5 zips remain. Which app/picker the tester uses. |
| C14 | Chrome. | `http://127.0.0.1:51780/_mcvs/status` loads (Host check passes, `superPassword` masked); `/_mcvs/ui/` shows the view-only page. Freezer timing: `adb shell device_config get activity_manager_native_boot use_freezer`, `adb shell dumpsys activity | grep -A 20 'Apps frozen:'`, reload after 90 s in Chrome, `adb logcat | grep -i freecess`. |
| C15 | Android 5–9 (if a phone is available). | Share via `file://` (StrictMode) reaches Gmail; otherwise the text report works; a file manager can write `McvsOffline/import/`. |
| C16 | LAN mode on. | `/_mcvs/*` from another device → 404; the redeem code does nothing (`ignored: LAN mode is on …`); Federation requests of the LAN guest work (the Host check is off in LAN mode); the game on the host still plays. |
| C17 | Regression. | Boot, matches, shop, signature screen, saves, unlock codes as in build 8; **no `gate: refused Host` WARN** in `server.log` for a whole session (the Federation Host check). |
| C18 | Layout. | Screenshots in landscape on the S22 and on a 360-dp phone, with the keyboard open (paste box, log search), at 130 % and 200 % system font size: the action bar and dialog buttons stay reachable. Switch dark mode while the page is open (activity re-creation): the session is kept, no "open from the game" banner. Hardware Back with an unsaved setting asks first; without, it closes the browser, also after a dialog, a Save or a Discard (edit, Back, Stay, Save, Back: the browser closes). |
| C19 | JNI table. | Status › host › `missingJni` is empty; every capability that should be on is on. |
| C20 | Probation revert (optional, developer). | Stage an import of a profile whose `_mcvs.gameProfile` was damaged by hand; start three times: the third start reverts (history `reverted`, the banner), the game loads the previous save. |

## 13. Open questions that need the phone

| # | Question | Answered by |
|---|---|---|
| Q1 | Is `BrowserComponent.IsInGameBrowserInitialized` true in an offline session, and does the native adapter pass our URL (with its fragment) to `InGameBrowser.OpenURL` unchanged? Is closing a JNI-opened browser safe when the native browser was never initialised? | C2–C4 |
| Q2 | Does the S22's WebView (Android 16) accept the `HttpOnly; SameSite=Strict` cookie for 127.0.0.1, also on a 303 in post mode, and which clipboard path works? WebView version? | C3, C10 |
| Q3 | Does the game thread stop while the browser is shown (STALL lines without the lease), and how long does the return take? | C5 |
| Q4 | Does a custom label in the Account button list show as typed or as a raw localization key? Which icon looks right? | C6 |
| Q5 | Do the inbox message's button (`btn_claim`) and expiry text suit a link-only, reward-less message? Is it re-flagged new after a refresh? | C1 |
| Q6 | Does the MediaStore Downloads URI grant through `ACTION_SEND` work for Gmail, Telegram, Discord, Drive on Android 10–16, and does the mirror fix end the 0-byte uploads? Which app and picker does the player upload from? | C11, C13 |
| Q7 | Android 5–9: is the StrictMode + `file://` share acceptable, or text-only reports? | C15 |
| Q8 | Does `MainActivity.a(true)` relaunch the game on Android 10+ (the alarm of a dead app under the background-start rules), and is one restart exactly one new process and one boot? Or is `host` better? | C12 |
| Q9 | Should an export also carry current-season leaderboard entries and `social/<id>`? Should an import set `Account.Data['alias']` from the bundle when the local account already has a different alias? | owner decision after C11 |
| Q10 | Where exactly does native Gaia keep the anonymous GLUID/password (KeyProvider `gameloft_sharing` `glshare` row vs a native file in `getFilesDir`)? Kept across updates (expected), lost on uninstall/clear-data (expected)? Decides how PLAYER-SETUP explains when an import needs the relink. | compare `accounts.json` credentials before/after an update and after clear-data |
| Q11 | Samsung's own background freezing: does it change anything for the in-game browser (it should not: same process, foreground)? | C5, C14 |
| Q12 | Does `ForceSaveProfile` on the client World reach the TS and save within a few seconds after the browser closes, or should `RequestGameSave` call it on the TS World directly? | C11 (a), C12 |
| Q13 | How big is the transfer text of a real unlock-all profile, and which text viewers let a player select and copy a 60,000-character part? | C11 (d), (e) |
| Q14 | Can Samsung My Files (One UI on Android 16) write into the game's `Android/data` folder? | C11 (g) |
| Q15 | How long does the boot apply take for a 1.4 MB profile, and does the first Federation request ever reach a closed port? | C11 (h) |
