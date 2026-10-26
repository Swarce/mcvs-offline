# Status

What is implemented, how it was checked, what the device tests showed, and the checklist for the next device sessions.
Last updated for build 8 (October 2026), after device test 6 (Samsung Galaxy S22, Android 16; see "Build 8", "Build 7"
and "Device test 6" below).

**Summary:** the Federation server is complete for the offline single-player goals and passes 739 tests, including a
conformance harness that runs the game's own client libraries against it. Device test 1 put the player build (patcher +
embedded host) on a phone for the first time and found two blockers (a stall with the internet on, a crash of the headless
TS World). Build 2 fixed both: in device test 2 the whole boot chain up to the TS handshake ran, and the TS World loaded its
level. Then the game hung on "connecting", with the game thread asleep in a futex. Build 3 gave the server Worlds
single-threaded physics, let only one World step at a time and added thread dumps to the watchdog: **in device test 3 the
game booted to the menu and played matches**; it found two crashes (unlock-all, the shop), fixed in build 4. **Device test
4: boot, bot matches, saves, the shop and the unlock codes work**; open were the installer stalling at 0 MB / 0 % on a
copied `Data/` folder and the avatar editor freezing after unlock-all. Build 5 adds the offline data install, an optional
64-bit-only APK (`--abi arm64-v8a`; 32-bit is kept by default again) and live ops (weekly division seasons, daily missions, weekly events).
**Device test 5: live ops and the copied-data install work**; leaving the firing range took ~11 s on "connecting to the
server". Build 6 makes the CRM document the game reloads there ~78% smaller for an unlock-all profile: **in device test 6
the return to the menu was fast**, and a thread dump finally caught the kill-signature screen freeze, which build 7 patches;
device test 7 is next (checklist below).

## Build 8 (October 2026): 'debloat', dead SDK pieces removed

New default patch id `debloat` (ARCHITECTURE.md §2 lists every item). Only what the APK research rated high-confidence and
free of any feature change; everything with a feature cost or a medium rating was left as it is (boot-completed reminders,
VIBRATE, BILLING, Google Analytics, FacebookActivity, the Play Games/Facebook app ids, the ad guard, dex code, Bind*.dll,
btls).

* **Manifest**: 113 → 46 elements (44 targets and their 23 intent-filter children). `BinaryXml` gained a checked element
  tree and `Remove`; the string pool, resource map and every kept chunk stay byte-identical. Refused (nothing written)
  unless every target matches exactly once in the verified 1.17.32 manifest.
* **Files**: the 9 Crashlytics/Fabric entries, again only if all are the verified ones.
* **Gameloft.Engine**: Crashlytics can never start; no push-token request (local reminders unchanged, the notification
  toggle still purges and re-schedules them); Facebook and Play Games are not initialised at menu start, so no
  GoogleApiClient connect, no possible Google sign-in prompt and no Facebook auto-login; a tap on their buttons in
  Settings > Account shows "Connecting…" and then the normal login-failed state.
* **Checked**: aapt2 `xmltree` (deletions only) and `badging` (package, launcher, exactly the kept permissions), apksigner
  v1/v2/v3 with `--min-sdk-version 21`, an independent AXML rebuild byte-identical to the patcher's, no reference to a
  removed component in the dex, the native libraries or the managed code, update install over build 7 (only removals,
  package-scoped permission). Gameloft.Engine: 9 patched + 3 added methods ILVerify clean and JIT, every other of 21,381
  method bodies unchanged, execution tests with the real assemblies and a native-call recorder. Two review findings (the
  "Connecting…" label after a login tap, the local-alarm purge in push registration) were fixed and re-verified.

### Also check in device test 7 (with build 8 installed)

1. Install over build 6 or 7: the update installs and the save is kept.
2. No Google sign-in prompt at boot; Settings > Account: Facebook and Google buttons show the failed-login state after a tap.
3. Local reminders still arrive (and after a reboot); toggling notifications off and on in settings works.
4. Shop, matches, saves and the signature screen as in build 7.

## Device test 6 (October 2026): fast return to the menu; the signature-screen freeze caught in a dump

Leaving the firing range is fast with build 6. The kill-signature (avatar) screen still froze after unlock-all; logcat
`25_10-20-06-49`: watchdog STALLs at 5 s and 15 s with the game thread (tid 27705) in state R, then the Mono managed dump
(the `logs.zip` upload was 0 bytes again; the logcat carries the dump):

```
string.op_Equality <- InternalStringComparer.IndexOf <- List<string>.Contains
  <- PlayerProfileComponent.GetSignatureAssetsList <- GetSignatureAvatarsList
  <- GameOfflineStore.GetKillSignItems <- GetSignatureList <- NativeGetSignatureList <- MenuComponent.OnInvokeDelegate (UI)
```

The build-5 analysis below looked only at `NativeGetSignatureLibrary` (one call) and wrongly put the cost in the Flash
VM. The screen also calls `GetSignatureList`, whose `GetKillSignItems` looks up each owned avatar's image with
`profile.GetSignatureAvatarsList().FirstOrDefault(...)`. Every one of those 1,512 calls rebuilds the 1,518-row list and
sets each row's `locked`/`isNew` with `List.Contains` on the owned and new lists (1,512 each): about 1,512 × 1,518 ×
3,024 ≈ 7 billion string compares. Measured on net8 with the real Game.dll: 26.8–33.8 s for one `GetKillSignItems("avatar")`
(the phone's Mono JIT is slower still). The same list builder is behind the 18 s and 27.6 s stalls of the unlock-all reward
popups in device test 5 (two builds per kill-signature reward, `_NotifyBundleReceived`).

## Build 7 (October 2026): the 'signatures' patch

New default patch `signatures` (Game.dll, `patcher/Patches/SignaturesPatch.cs`), both methods checked against their
1.17.32 IL fingerprint first:

* **G1** `PlayerProfileComponent.GetSignatureAssetsList`: an owned or new list of more than 16 ids is copied once per call
  into a `HashSet<string>` (the same default comparer as `List.Contains`) and read through `ICollection<string>.Contains`;
  shorter lists are used as they are. Every caller gains: the shop's availability check per kill-signature item,
  `GetSignatureObject` (matchmaking, HUD, leaderboards, the match-over sync of every player's card: bots own all avatars),
  level-up and reward popups. One call at unlock-all scale: 21–41 ms → 0.34–2 ms on net8.
* **G2** `GameOfflineStore.GetKillSignItems`: the avatar and background lists are built once per call and reused for every
  owned item (once per owned item before). The signature screen at unlock-all: 26.8–33.8 s → 49–104 ms on net8 (×380 or
  more); a half-way profile (759 owned, 759 for sale): ~14 s → 0.3 s.
* **Checked**: a differential test runs the original and the patched real Game.dll side by side on net8 (fake database
  and profile, real `OfflineStoreData`): ~60 cases of `GetSignatureAssetsList` and 56 of `GetKillSignItems` (null and empty
  lists, the 16/17 threshold, duplicates, unknown and null ids, case variants, missing tables, unlock-all) give identical
  rows, flags, dictionaries and exceptions (`RealSignaturesPatchTests`; `MCVS_SLOW_TESTS=1` also runs the original screen
  at full unlock-all scale). ILVerify against the game's BCL passes, both methods JIT, and a second patch run refuses.
  Three independent reviews (IL, behavioural equivalence, remaining cost) found no defect.
* **Left as it is** (minor, measured): for a player who owns about half the avatars, the shop part of the screen still
  builds the list once per avatar for sale (`GetKillSignItemObject`), ~0.3 s on net8, ~0.5–0.9 s on a phone; reward popups
  after unlock-all cost ~0.25 s per batch of 400 on net8 (keep `unlocks.batchSize` ≤ 400). A follow-up patch (one list per
  screen refresh for the shop part too) is noted in ROADMAP.md if device test 7 shows a hitch.

### What to check in device test 7

1. **Signature screen** after unlock-all: it opens within about a second; owned avatars, backgrounds and sounds show and
   can be equipped; with a profile that is not fully unlocked, the unowned ones can still be bought.
2. **Match results / return to the menu**: less stutter than before (G1 also covers every player card's signature).
3. A new unlock-all grant (fresh profile): the reward popups no longer stall for 18–27 s.
4. Any `[watchdog] STALL`: send the logcat (the managed dump is in it).

## Device test 5 (October 2026): live ops and the data restore work; firing-range exit lag

Live ops (seasons, daily missions, weekly events) and the install from a copied `Data/` folder worked. Leaving the
firing range sat on "please wait, connecting to the server" for ~11 s (19:43:18.6 → 19:43:29.5 in the logcat), all of it
on the game thread (tid 25142 runs the client, TS and GS Worlds); the server answered every request in 0-4 ms (92 ms for
the save). The log files arrived as 0-byte uploads again; the logcat was enough.

What the TS does on every return to the menu, after a match as well (`GameServerComponent.ResumeServer`, the client sends
it from `GameplayComponent._ExitGameplay`; the firing range only differs by `IsPractice`):

| Step | Request | Game-thread time |
|---|---|---|
| `ReloadProfileAsync`: load and apply the game-profile dump | #61 GET `_mcvs.gameProfile`, 557,204 B | 3.4 s |
| its forced CRM refresh (`RefreshCrmConfig(forceRefresh: true)`): parse twice, apply (`RefreshDatabases` reloads `MCVS_Meta`/`MCVS_System` from disk because the document carries `_OverrideTables`), sync the 400 KB text to the client, which parses it again, plus the native engine | #62 `configs/users/me`, 402,801 B; then #63 events | ~4.4 s |
| `SaveProfile` (`MakeSureProfileIsUnlocked` first) | #64 POST dump, #65 merge | 0.5 s |
| `DoSaveProfile`'s own CRM refresh: parsed twice, then found equal (`StandardCrm.Data.Equals`) | #67 `configs/users/me`, 402,801 B | 1.2 s |
| client: `StartSynchronization`, shop and UI refresh | | ~1 s |

99% of the CRM document (399 KB) is the generated `offline_store`, and 1,512 of its 1,858 entries are kill-signature
avatars (35 more are backgrounds). The `[watchdog] STALL` at +119 s was the game in the background (`PlayerBase::pause`
4 s earlier), not a hang.

## Build 6 (October 2026): a smaller CRM document

* **Owned kill signatures leave the player's `offline_store`** (`server/Services/Crm/CrmOwnedStoreItems.cs`). The
  signature screen lists owned avatars and backgrounds from the profile and only the others from the store
  (`GameOfflineStore.GetKillSignItems`), so their entries were never shown. For an unlock-all profile the document drops
  from 402,636 to 89,282 characters (1,858 → 311 entries); a new player still gets everything. Sounds stay listed: the
  screen takes their owned list from the backgrounds (a game bug), so the store is where they show up.
* **When the owned set changes**: only when the game-profile dump is read (the TS does so at login and in each
  `ReloadProfileAsync`; the storage module publishes `storage.gameprofile.read`). The fetch right after that read is the
  forced refresh, which applies the document anyway; the one after each save gets the same text, which the TS recognises
  without re-applying it. A newly bought avatar leaves the store at the next return to the menu (until then the screen
  already hides it as owned).
* **Fail-safe**: a dump in an unexpected shape (a list that appears twice, or holds anything but ids) logs
  `CRM: owned kill signatures of ... not read (...)` and serves the whole store, as before. `crm.omitOwnedSignatures:
  false` in settings.json turns the filter off.
* Not changed (game code): the 557 KB profile reload, the two CRM fetches per return and the database reload of the
  forced refresh. Those would need IL patches to the game's resume path; see [ROADMAP.md](ROADMAP.md).

### What to check in device test 6

1. **Firing range**: leave it and time "connecting to the server" (build 5: ~11 s). The server log line
   `CRM: offline_store for <account> leaves out N owned kill signature(s) (... avatars, ... backgrounds owned): 402... -> 89... chars`
   appears once per return to the menu, and `[http] #.. GET /configs/users/me ... -> 200 [crm.config]` shows ~89,000 B.
2. **After a match**: the results screen and the return to the menu stutter less.
3. **Signature screen** (avatars, backgrounds, sounds): owned ones are listed and equippable, the rest can still be
   bought; buy one and check it stays owned after the next match.
4. A `CRM: owned kill signatures of ... not read` warning means the dump has a shape the scanner does not expect: send
   the logcat.

## Build 5 (October 2026): offline data install, 64-bit only, live ops

`main-dev` after merging `feat/install` and `feat/liveops`; APK `mcvs-offline-<commit>.apk`, same signing key as builds
1-4, so it installs over build 4 as an update and keeps the data and the save.

* **Data already on the phone is accepted without Gameloft's CDN** ([INSTALL-OFFLINE.md](INSTALL-OFFLINE.md)). Why a
  copied `Data/` stalled: the Java installer starts offline only with its index `files/pack.info` in *internal* storage,
  `prefs/gl_ver` = 1173200 and every 2 MiB piece listed in its download journal. A copied `Data/` has none of that, so
  the installer went to the CDN for the index and every split file, and the CDN's rate limit (HTTP 429) most likely gave
  the 0 MB / 0 % screen (not proven: no log of that run exists). Now, in Bootstrap before `GameInstaller` runs: the
  installer's index is saved to `files/McvsOffline/install/pack.info` every start (a backup of `files/` carries it); a
  missing or stub index is restored from that copy or a mirror's, with `gl_ver`, the journal and the installer's
  preferences, then CRC-checked in the background once the installer has handed over to the game (resumed at later starts
  until done; files still damaged at the next start are deleted and downloaded again); a
  `Data/` that is exactly the complete 1.17.32 pack (15,747 files, size per texture format) gets a rebuilt, size-only
  index; `InsTime` is refreshed so the weekly CDN version check never runs; `[installer]` lines log the installer's
  states. No `classes.dex` change; the installer's own checks still run. Settings `install.*`.
* **ABIs**: the patcher keeps every ABI by default (32-bit `lib/armeabi-v7a` restored after build 5); `--abi arm64-v8a`
  drops the 67 `lib/armeabi-v7a` entries (APK 57 MB -> 44 MB).
* **Live ops** ([LIVEOPS.md](LIVEOPS.md)), computed from the clock and `settings.json` (`liveops.*`), nothing stored by
  the server: a division season every Monday 00:00 UTC (17 division events, a league group of 100 with rivals that grow
  during the week and freeze at the end; the TS itself promotes, demotes and puts the season reward in the inbox), a daily
  mission event (3 of 6 objectives, a loot crate for all three), a weekly event (theme objective + 3, cosmetic crate
  rotating weekly) and a weekly modifier (standard, Coin rush, Trophy fever, Credit rush, MVP bounty, Rival surge) applied
  as CRM table overrides. The CRM `expiry` makes the running game refetch at each day and season boundary. Event and
  expiry dates are sent as `yyyy-MM-dd HH:mm:ssZ`: with the ISO `T` a day-first phone locale swaps day and month.
  `GET http://127.0.0.1:51780/_mcvs/liveops` shows the schedule.
* **Integration fixes**: event dates from `social/events.json` are now sent in the same `T`-less form; ILVerify against
  the game's mscorlib found two `InitOnly` uses in the live-ops schedule (fixed, now clean); `HttpServer.Stop` answered a
  connection accepted at the same moment with "503 server busy" instead of closing it (the cause of an intermittent
  `HttpServerTests` failure).

**How it was checked.** `scripts/build.sh` with `MCVS_REQUIRE_EMBEDDED=1 MCVS_REQUIRE_REAL_CLIENT=1`: McvsOffline.Tests
508/508 and McvsOffline.Embed.Tests 143/143, among them 19 installer-data tests (real ETC pack, both live indexes), the
real-APK patcher tests for arm64-only, and the live-ops conformance tests (16 days of CRM documents and events through
the real client's parsers and every event type's `Initialize`, under a day-first culture). ILVerify of the embedded DLL
against the game's BCL: no findings. A clock simulation of the composed server over loopback HTTP with the real ETC data
pack (2026-10-19 to 2026-11-04 in 3-hour steps): 3 seasons, 16 days, 4,680 events parsed, the CRM expiry always ahead
of the clock and never past the next boundary, the next season listed on the last day; three players posting division-5
trophies finished at rank 28, 61 and 93 of 100 (promotion zone 1-30, demotion 91-100), i.e. promoted, kept and demoted, in
both seasons, with the same rank on every later read (9 days) and the rival scores frozen. APK: `zipalign -c -p 4` OK,
`apksigner` v1+v2+v3 with certificate SHA-256 `fc0db84e...7d16`, `lib/` only `arm64-v8a`; the patched game assemblies
differ from build 4's only in their 16-byte module id (no new IL patch in this build).

### Review fixes to build 5

APK `mcvs-offline-<commit>.apk` of the review-fix commit, same key; it replaces build 5's APK for device test 5.

| Finding | Fix | Tests |
|---|---|---|
| After a *partial* restore (saved index, some files missing), the background CRC check started after a fixed 90 s, while the installer was still downloading: every missing or half-written file went into `damaged.txt`, and the next start deleted the files the installer had finished in the meantime, without looking at them again (offline, the game could then not start) | The check waits until the installer has handed over to the game (`GameInstaller.sbStarted` or state 21, read over JNI), then one more minute; only if that cannot be read *and* every file passed the size test does it fall back to the fixed wait. Missing files and files of another size are never listed (the installer's own size test fetches them). The next start reads each listed file again against the installer's index (or the saved copy) and deletes only those that still fail; a file the installer finished or the player replaced is kept, one no index describes is left alone; at most 256 MiB are re-read at start-up, the rest waits for the next start | `InstallerDataTests`: `APartialRestoreWaitsForTheInstallerAndAFileItFinishedIsNotDeleted`, `TheDamagedListIsReadAgainAndOnlyFilesThatStillFailAreDeleted` |
| The CRC check ran only in the session that restored the index: if that session ended first (quit, crash, OOM), a zero-filled split piece of the right size, already in the journal from its size, was accepted for good | `Install()` writes `McvsOffline/install/verify-pending` (index hash and position); every start with it present schedules the check again ("CRC check pending" in the `[install]` line); the check saves its position every 64 MiB and resumes there for the same index (from the start for another); the marker goes once every entry has been read | `ACheckThatDidNotGetThroughIsScheduledAgainAndResumes` |
| With `liveops.seasonDays` 1 (documented as valid) no season object resolved: the key of a one-day window has no `x<days>` suffix, but season ids were parsed as multi-day only, so the CRM listed ids for which `/events` had no definition and the division boards fell back to the static rivals | `LiveOpsWindow.Parse` reads a bare `yyyyMMddHH` key of a season-type id as one day, and accepts only the exact form `Key` writes (`x1`, `x07` refused), so a rebuilt id equals the requested one | `LiveOpsTests.EverySeasonLengthGivesIdsThatParseBack` (1, 2, 3, 7, 28 days), `EveryListedEventIsServedParserSafeAndDeterministic` (7, 3, 1 days), the real-client conformance loop also with 1-day seasons (289 division events, 16 weekly events initialized) |

Checked: `scripts/build.sh` with `MCVS_REQUIRE_EMBEDDED=1 MCVS_REQUIRE_REAL_CLIENT=1`: McvsOffline.Tests 516/516,
McvsOffline.Embed.Tests 146/146; ILVerify of the embedded DLL against the game's BCL: no findings; APK `zipalign -c -p 4`,
`apksigner` v1+v2+v3 with certificate SHA-256 `fc0db84e...7d16`, `lib/` only `arm64-v8a`.

### What to check in device test 5

1. **Update install** over build 4: the save is kept; `[install] installer data: ...` in the bootstrap log says the
   installer's index was saved; the installer screen passes as before. Note the `[installer] ... finished after N s`
   time (about 16 s in device test 4).
2. **First season start.** The first CRM fetch lists the season events; the game starts its first season *without* a
   season-end evaluation, and the game itself resets the division trophies collected before (`Data` was never set
   because no events existed). From then on the Divisions screen shows a group of 100 and the season end on Monday
   00:00 UTC in local time. This week's modifier (season 2026-10-19) is Trophy fever; the season from 2026-10-26 has
   Credit rush. Check the dates read right on the phone's locale.
3. **Season end** (Monday 00:00 UTC, or set `liveops.seasonDays`/`seasonStart` to see it sooner): the season-end screen,
   promotion or demotion by one division, the "DIVISION REWARD" inbox message; restarting the game afterwards must not
   give it again.
4. **Daily missions and the weekly event** show their objectives, count matches and pay out; at the reset hour the day
   rolls over while the game is open (CRM refetch, no `[watchdog] STALL`).
5. **Copied `Data/` folder** (optional, the device-4 stall): with a backup of `files/` or only `Data/`
   ([INSTALL-OFFLINE.md](INSTALL-OFFLINE.md)): uninstall, install build 5, copy, start in airplane mode; the
   `[install]` line says `index restored ...` or `index rebuilt ...` and the game starts without the CDN. Measure the
   first start (the size check of 15,747 files runs on the UI thread in `OnCreate`; with a rebuilt index on every start).
   About a minute after `installer finished after N s`, `installer data: checking the CRC32 of every file` and later
   `background check done` (or the damaged files) appear; closed before that, the next start's `[install]` line ends in
   `CRC check pending` and the check resumes.
6. **Avatar editor** after unlock-all: a thread dump of the freeze, sent from Downloads > MCVS-Offline (see "Device test 4").

## Device test 4 (October 2026): shop and codes work; installer stall and avatar editor

Same phone, build 4. Boot, bot-filled matches, saving, the shop and the unlock codes work. Two problems remained: after
reinstalling and copying the `Data/` folder back, the installer screen stalled at 0 MB / 0 % (addressed in build 5,
above), and the kill-signature avatar editor froze after unlock-all (below). Logcat `25_10-12-30-37` is a normal boot of
this build: 56 requests, all fast; the main menu at about +60 s; the installer screen took about 16 s.

### Open: the kill-signature avatar editor freezes after unlock-all (solved in build 7, see "Device test 6")

Logcat `25_10-12-30-37`, second watchdog STALL (`1792945929.086`): opening the avatar (emblem) editor with every avatar
owned, the game thread (`Thread-22`, tid 31784) is in state R with `+2542 ticks` (25 s of CPU time since the watchdog's
previous snapshot), and no server request is in flight. The managed thread dump of that stall (`logs/threaddump-20261025-163218.txt`)
arrived empty (0 bytes), so the frames on the game thread are not known yet.

What the code does (Game.dll, Gameloft.Engine.dll, `Menus/UI.swf`):

- The editor is ActionScript (`MenuCustomization` in `Menus/UI.swf`), run by the engine's native Flash VM on the game
  thread. It calls the native function `GetSignatureLibrary` once, which is
  `PlayerProfileComponent.NativeGetSignatureLibrary`: three `GetAssetsObject` calls (avatars, backgrounds, sounds), each
  `GetSignatureAssetsList` over the **whole** table (`KillsignatureAvatars` has 1,518 rows) with
  `List<string>.Contains` on the owned and "new" lists, then `OrderBy(locked)`. With 1,518 owned avatars that is about
  2.3 million string comparisons, far below a second; nothing in it is quadratic in a way that reaches 25 s.
- The result reaches the VM as an array of 1,518 `ASManagedObject`s (`ASValue.SetArray`/`SetManagedObject`); the
  ActionScript reads their fields through `ASManagedObject.GetMember`, whose reflection lookups are cached
  (`MoreReflection` caches per type), so per access it is a dictionary lookup and a property get.
- **The editor always receives all 1,518 rows, owned or not.** Unlock-all changes only the flags: every row becomes
  unlocked, and every granted avatar is also "new" (`KillSignatureNewAvatars`). So the extra work is what the
  ActionScript does for unlocked and new entries (an emblem image and a "new" badge per tile instead of a lock), which
  happens in the native VM and its texture loading, not in managed code.

Not patched: no managed hot spot was found to cache, and the managed-side levers (hiding or capping entries, clearing
the "new" flags) change what the player sees and are not clearly safe without the stack. Next, on the phone: send
`logs/threaddump-<time>.txt` of such a stall (a dump whose game-thread frames sit in `ASManagedObject.GetMember` or
`GetSignatureAssetsList` would point back at managed code; frames in `(wrapper managed-to-native)` UI calls confirm the
VM), and say whether the editor opens after waiting one to two minutes and whether a second opening is fast.

The logs sent again with build 5's merge (`bootstrap.log`, `game.log`, `server.log`, `threaddump-20261025-163218.txt`)
were again **0 bytes**, while the logcat of the same run shows every log file being written without an error. Those are
the names under `Android/data/<package>/files/McvsOffline/logs/`, which other apps cannot read on Android 11 and later, so
a file picker hands over empty files. Send the copies in **Files > Downloads > MCVS-Offline** instead
(`<session>-bootstrap.log.txt` and so on); the watchdog writes the whole thread dump into `bootstrap.log` as well, so
that copy carries it.

## Device test 3 (October 2026): boots and plays; two crashes

Same phone, build 3 (default patch set, `--game-log all`), logcat from LogFox (three runs: 22031 unlock-all crash, 22595 the
slow next start, 24715 shop crash). **The game boots to the main menu and plays matches**: the device-test-2 physics fix
works. Every server request was fast (the slowest a few tens of ms).

### Bug 1: unlock-all lags, then crashes ("Could not find merc data for mercId 'ALL'")

* Seen: with `unlockAll: true` (and the `UNLOCKALL` coupon, which uses the same "mercs" group), the player level jumps to 46,
  level-up rewards tick up, the game lags, then dies with `System.InvalidOperationException: Could not find merc data for mercId
  'ALL'` in `LotteryComponent.DisplayLotteryRewards`, fatal (an unobserved fire-and-forget task, `GameApplication.UpdateWorlds`;
  logged four times: stdout, our crash hook, the game's handler, mono-rt).
  The next start was slow; once everything was claimed, unlock-all worked.
* Cause: the "mercs" unlock group was the single reward `UnlockMerc_ALL_MAX`. Claiming it works (`UnlockMercRewardItem.OnClaimed`
  -> `MercsComponent.CheatUnlockAllMercs(profile, "MAX")`), but it is an `IPrintableRewardItem`, so the reward display shows it,
  and the display looks the merc up by its id (`GetBuiltMerc("ALL")` -> null -> throw). The reward-string grammar is
  `Type_Subtype_Subtype2...` split on `_` (GM/RewardItem.cs Parse); `"ALL"` is only understood by `OnClaimed`, by nothing that
  displays a reward.
* Fix: the "mercs" group (tools/content-gen, `merc_unlock_items`) is now `UnlockMerc_{2-21}` (every displayed merc but the
  starting merc 1, whose reward would only turn into its alternate reward, 40 random gear) followed by `ModTier_{1-21}_4`
  (`ModTierRewardItem.OnClaimed`: every mod of that merc with `Tiers <= 4`, perks included at tier 4; mod names in the catalogue
  go up to Tier4). `UnlockMerc_<id>` takes the plain merc path of every display (`GetBuiltMerc(<id>)` exists for 1-21);
  `ModTier` is not printable, so the lottery skips it, and the inventory diff / inbox claim it after its merc
  (`ModTierRewardItem.IsAvailable` needs an unlocked merc; the diff keeps the ledger's key order, only `MercSkin_*` moves last).
  41 rewards instead of 1; UNLOCKALL then had 11374 (11368 once the 6 unreleased avatars left it, below). The conformance test parses the UNLOCKMERCS coupon with the real
  `RewardHelper.CreateRewardItem` and checks every id is a numeric merc.
* An inventory ledger granted by build 3 still holds `UnlockMerc_ALL_MAX` (already claimed). `UnlockGranter` counts the
  whole "mercs" group as present then (`LegacyMercsKey`), so the per-merc keys are not written over it: each would pay
  the game's duplicate compensation (40 random gear) for a merc the player already has.
* Also fixed by build 4's 'store' patch (bug 2): every `KillSignature_Sound_*` of the signatures group made
  `RewardItem.GetRewardInfo` (which calls `IsAvailable`) throw in the reward popups and the lottery.
* **The level jump is a known one-time behaviour, not changed.** Levels come from the mods: `PlayerProfileComponent.UnlockModForMerc`
  calls `AddXp(modSpecs.PlayerXP)` for each newly unlocked mod (21 mercs x 23 mods), the same with `UnlockMerc_ALL_MAX` or
  `ModTier`. Every XP change recomputes the level; each level crossed runs `OnPlayerLevelUp` (its kill-signature/credits rewards
  are claimed at once, no popup each) and a fire-and-forget `SaveProfile` of the whole (now large) profile: run 22031 made 50
  `POST /profiles/me/myprofile`, each followed by the same `DELETE /messages/secured/me` of one long-deleted message and
  about every second one by a reload of the 400 KB CRM document (`GET /configs/users/me`), all parsed on the game thread.
  That is the lag; it ends once the grant is done. The slow next start (run 22595) was the grant continuing: the crash had
  stopped it after 801 of 11334 keys, so the next start diffed the remaining 10533. A mitigation would mean patching XP or
  save batching in the game; not small and not clearly safe, so not done.

### Bug 2: opening the shop crashes at once

* Seen (run 24715): `NullReferenceException` in `Game.KillSignatureRewardItem.GetAvailableId(PlayerProfileComponent)` [IL 0x84]
  <- `IsAvailable` <- `GameUnifiedStoreComponent.<>c__DisplayClass25_0.<SelectItems>b__8` <- `SelectItems` <- `GetStoreData`
  <- `TryDispatchStore` <- `NativeSetStoreCallback`.
* Cause: `GetAvailableId` builds its candidate list only for "Avatar" and "Background"; for "Sound" the list stays null and
  `list.Count` throws, so `IsAvailable` throws for every `KillSignature_Sound_*` our `#VersusCoins` hub sells (the official
  store never listed sounds). The SelectItems filter (`x.Content.FirstOrDefault()?.RewardPrototype.IsAvailable(profile)`) is
  evaluated for every item when the shop opens.
* Fix: new default patch **'store'** (patcher/Patches/StorePatch.cs, Game.dll, both methods fingerprint-checked):
  S1 the "other subtype" branch of `GetAvailableId` returns `Subtype2` for a sound the player does not own yet
  (`KillSignatureSounds.Contains`, the same test `OnPrepareToClaim` makes), null for a random sound or any other subtype, and a
  null `Subtype2` returns null; avatars and backgrounds run the shipped code. S2 the SelectItems filter lambda runs in
  `try/catch (Exception)`: an item whose check throws is left out of the shop and reported once per id as
  `[McvsOffline] store item <id> threw <exception>` (stdout/logcat, bootstrap.log via `GameHooks.StoreItemException`; the
  crash report counts them). Both executed against the real Game.dll on net8 (RealStorePatchTests).
* The other hub rewards (MercWeapon, GearSlot, ModTier, crates, VIP, packs, `WeaponCamo_<merc>_Random`,
  `MercAccessory_<merc>_Random`) were read in the decompiled `IsAvailable`/`GetRewardInfo`: no null path for the ids we generate
  (GearSlots always has 6 entries, slots are 1-6; crates check their contents, where a random sound now is simply unavailable).
  Not verified on a device: the serialisation of each hub entry (`SerializeObject` -> `GetObject`) is not covered by the safety
  net; a throw there would still stop the shop.

### Also in build 4: opt-in codes for paid and unreleased content

`UNLOCKPAID` (VIP-pool-only camos and jerseys, 5,644 rewards with the mercs first; split as `UNLOCKPAIDCAMOS` /
`UNLOCKPAIDACCESSORIES`) and `UNLOCKUNRELEASED` (6 never-released kill-signature avatars, 1513-1518). The unreleased set
is no longer sold or part of UNLOCKALL/`unlockAll` (UNLOCKALL: 11,368 rewards). How the sets are decided, the caveats
("no shipped source" alone would wrongly catch Witch and Bli-P, which the live server sold) and every exclusion:
docs/ECONOMY.md "Paid and unreleased content". Not tried on a device yet.

## Device test 2 (October 2026): "connecting" forever

Same phone (Samsung Galaxy S22, Android 16), build 2 (`d9a433f`: hook, headless, headless-engine, nativeonline, geo, bottile,
finder; `--game-log all`), **internet on**, installed over build 1 (same key). One run, logcat from LogFox.

### What worked

Everything up to the TS handshake, as predicted. Hook at +0.008 s, `args.txt is up to date` (with `-DataCenter mcvs`), server
on 127.0.0.1:51780, CRM document built; the native online SDK was skipped twice (client World and TS World). Requests #1-#25
in the predicted order with no `datacenters` request: client session, LocalAnubis super session, QuickLaunch,
`lobbyconfigs ts`, Controller login, `connect game` (TS World created), the TS World's own Federation, CRM
`configs/users/me/game` 596 ms after the TS session, TS lobby login. **The TS World loaded `Levels/Menus.level` with no
`Fatal` and no `[McvsOffline] headless world:` line**: the device-test-1 headless fixes work. The native IAP library reports
`initialized: yes` without GaiaInit (this answers the open question of device test 1).

### The hang

* `#26 POST /authorize` on behalf of the player (the TS handshake: `Connection.ProcessHandshake` ->
  `OpenSessionForUser`), answered 200 in 0.0 ms. The game logged `[w2] {FedRequestHandler.cs}.SendAsync:48 | Request
  .../authorize took 5 ms` on the game thread at 953.755 and then **nothing at all, on any thread**, for 60 s (logs/game.log
  has exactly the logcat entries). For every earlier request a second line, `{RequestHandler.cs}.SendAsync:73 ... returned
  completed with code : OK`, followed; for #26 it never came.
* Watchdog: `STALL: no game-thread work item ran for 5940 ms; frame clock (totalTime) unchanged for 5004 ms`, then at 15 s
  `game thread 26748: 'Thread-22' state S wchan futex_wait_queue_me syscall ?`: the game thread was **asleep in a futex**
  (a lock or condition wait), not spinning, not in the network. Java stack: `"Thread-22" RUNNABLE` with no Java frames.
* The heartbeat says where: the last beat ran at 952.916, the frame clock last moved by 953.85, and the beat posted at
  about 953.86 (to `GameApplication.Context`, which raises `WorkItemAdded`) never ran. So the game thread blocked **in the
  frame that ran the TS handshake continuation, after the TS World's work items** (`World.UpdateUnhandled`: `ExecuteItems`,
  then `_Update`), in a wait that does not pump contexts. The #26 continuation had gone asynchronous and was queued on the
  TS World's context, which only that thread drains: hence the missing second line.

### Cause (leading hypothesis, not yet confirmed on a device)

The facts below are verified in the decompiled engine, the IL and libnativeengine.so
(`/home/user/mcvs-work/reports/device2-deadlock.md`, tags [V]); that this overlap is what put the game thread to sleep is an
inference (that report's [I], likelihood high): no device run has shown the game thread inside a Havok wait yet.

* `GameOptions_android.json` (profile `Specific`) turns on **multithreaded Havok physics** for the whole process
  (`PhysicsComponent.PhysicsMultithreaded = true`, the only override that turns it off is never selected), and GPU defaults
  turn on **deferred physics** (`DefaultDeferred = true`). The options are applied in `LoadGameDefinition`, before the client
  World exists. Every World's `PhysicsComponent` constructor passes `PhysicsMultithreaded` to the native constructor.
  `Deferred = !World.Headless && DefaultDeferred`: the client steps on a `LoadingThreadPool` thread
  (`PhysicsComponent.UpdateSimulation` -> `World.RunTask`), a headless World steps on the calling thread (the game thread).
* Native side: the `hkCpuThreadPool` and the `hkJobQueue` are **process globals**, created once (NE 0x37fab4, `[0x16ff440]`,
  `[0x16ff448]`); every multithreaded hkpWorld registers with that one queue and `PhysicsComponent_UpdateStep_private`
  calls `hkpWorld::stepMultithreaded(world, queue, pool, dt)`. The pool's `processJobQueue` and `waitForCompletion` take no
  lock and share one "processing" flag, one worker callback slot and one completion semaphore: **one stepping thread at a
  time**. The waits are `hkSemaphore::acquire` = `pthread_cond_wait`, i.e. `futex_wait_queue_me`.
* A World steps physics only while it plays a level. The TS World started playing `Menus.level` just before the handshake
  (about 53.7). From then on, every frame the client's step ran on a pool thread **while** the TS World's step ran on the
  game thread inside `w2._Update`, through the same pool and queue, with nothing serializing them (`WaitDeferred` only orders a
  World's step against its own previous one). Within a few frames a thread waits for a completion that never comes.
* Gameloft never ran this mix: their dedicated servers ran one World per Linux process and skip GameOptions entirely
  (`GameOptionPerformanceProfileComponent.OnCreate` returns on Linux), i.e. single-threaded, non-deferred Havok; the Android
  client has one World.

Which Havok wait the game thread sits in, and whether it sits in one at all, cannot be settled statically (the build 3
thread dump will show it); that the two steps overlap with no synchronisation is verified. **Ruled out** (same report and
`/home/user/mcvs-work/reports/device2-ourcode.md`): the context-pump mechanism (no game-thread wait in the frame loop pumps
contexts; `Join`, `WaitLoading` and `Send` have no callers; `DelayedPost` has none either), the HTTP continuation chain (no
blocking call), the loggers (`DeviceLogger_Log` is a bare `ret`; our bridge only enqueues), MenuComponent/SWF in the TS
World (both `[KeepInHeadlessMode(false)]`), the network (futex, not poll/recv), and **our own code**: everything of ours on
the game thread is bounded and was idle, our IAP P/Invoke first ran 33 s after the hang and takes no lock, and after the
hang our threads still JIT-compiled, allocated and made JNI calls (no runtime-global lock held). The headless patches are
not wrong; they let the TS reach code that had never run on Android.

**Not ruled out: the render path.** In `RenderThreadMode.Full` there is no managed cycle (the render thread waits for
`UpdateSignal`, which the game thread sets before its own wait) and the native render-queue lock is a spinlock, but it is
not verified that this phone runs Full. The GPU defaults in `GameOptions_android.json` are `enableRenderThread false`,
`renderThreadMode 0` (None); only the named GPU profiles (VeryLowGPU to High_Ultra) set `true`/`3` (Full). The S22
(SM-S901E, Adreno 730 per the device-test-2 logcat) is named by no rule (they list Adreno 3xx-6xx, Mali, PowerVR, Tegra and
a few models); it gets MidGPU only through the last rule, `MidGPU: CORES>1`, a catch-all whose evaluation is native and was
not checked. In None mode `Render()` runs on the game thread (`GameApplication.cs:883-895`), a path that was not analysed.
The build-3 `[boot] physics:` line prints `RenderThreadEnabled`/`RenderThreadMode` and settles it.

### Fix (build 3)

* **`physics`, a new mandatory patch** (Gameloft.Engine.dll), each site checked against an IL fingerprint of the whole
  verified method first:
  * P2: the `PhysicsComponent` constructor passes `McvsPhysics.UseMultithreading(this)` to `ConstructNative` instead of
    `PhysicsMultithreaded`. Headless (TS/GS) Worlds get a **single-threaded hkpWorld** (simulation type 2, `stepDeltaTime`,
    never registered with the global queue: `tbz w19` at NE 0x37fa8c), as on Gameloft's servers; the client keeps
    multithreaded, deferred physics. The final answer comes from `GameHooks.PhysicsMultithreading`, which also logs it
    (`[boot] physics: ...`); without McvsOffline.Server.dll the IL alone picks "multithreaded only for client Worlds".
  * P1: `PhysicsComponent.UpdateSimulationSteps` (the only caller of `UpdateStep_private`) runs inside
    `McvsPhysics.EnterStep()`/`ExitStep()`, a **process-wide gate**: no two Worlds ever step Havok at the same time. The
    wait is bounded (2 s, then the step goes ahead without the gate and a warning is logged), so the gate can never hang the
    game itself; `WaitDeferred` is always called outside it, and nothing that runs inside a step waits for the game thread
    (contact callbacks post to the World's context).
  * Settings for A/B runs (defaults = the fix): `embedded.physics.serverMultithreaded` (false), `clientMultithreaded`
    (true), `stepGate` (true), `stepGateTimeoutMs` (2000).
* **`waits`, a new default patch, hardening with no observed trigger** (Gameloft.Engine.dll): both
  `TaskExtension.WaitAndWork` overloads wait in 100 ms slices (`WaitAny(handles)` -> `WaitAny(handles, 100)`). `Post`,
  `Send` and `EnqueueDelayedItems` raise `WorkItemAdded` and native tasks call `NativeTaskAdded`, so ordinary work always
  wakes these waits. The slices bound the two state changes in the shipped code that raise no event: a context re-enabled
  by `World.OnLoadedScene` (`Context.Enabled = true`; only `World_Disposing` waits with contexts), and the native wake-up
  callback, which the `NativeTaskAdded` accessors install and remove with an unlocked check-then-act. They do **not** cover
  a context registered to the thread after the wait began (`Currents` is a snapshot, re-drained every slice), and
  `DelayedPost` has no caller in the game. Every call site pumps exactly what it pumped before (the non-pumping
  `task.Wait()`/`signal.Wait()` paths are unchanged), so no work runs where or in an order it did not run before. Not
  related to this hang: no pumping wait was active.
* Our code: the IAP status read now waits for the first heartbeat and stops after the first "yes"; the watchdog reports a
  heartbeat that ever runs on another thread than the game thread. No interference was found.

### Diagnostics added for device test 3

| What | Where to find it |
|---|---|
| **Thread dump** at the 15 s and 180 s STALL (`diagnostics.threadDump`, default true), on its own thread `mcvs-threaddump`, never waited for: (1) native view, no suspension: every thread's state, wchan and `/proc/self/task/<tid>/syscall` (futex address, mapping, words, timeout, pc), read with `PR_SET_DUMPABLE` raised and restored (the reason for "syscall ?"), plus a stack scan (64-bit installs; the S22 runs arm64) of the game thread and the threads that share its name and the Finalizer, through `/proc/self/mem` (a bad address is an error, never a crash), raw `lib+0x...` first, then named (`dladdr`, `mono_pmip` for managed code); (2) Mono's own managed dump of every thread (`mono_threads_request_thread_dump`, printed by the finalizer thread to fd 1, which points at a file for at most 10 s) | logcat `McvsOffline` lines `[threaddump] ...`, the header `Full thread dump:` on tag `mono` (priority E), `logs/bootstrap.log` (copied to Download/MCVS-Offline), `logs/threaddump-<time>.txt` |
| The STALL snapshot's game-thread line now reads the syscall too (`syscall 98 0x... (futex: waiting for a lock or condition)`) | logcat `[watchdog] STALL` |
| Physics decisions and the engine's threading choices, once per World | `[boot] physics: client World #1: Havok multithreaded ...; engine threading: PhysicsComponent.DefaultDeferred ..., GameApplication.RenderThreadEnabled ..., RenderThreadMode ...`, `[boot] physics: server (headless) World #2: Havok single-threaded ...` |
| Step-gate counters (entries, waits, longest wait, timeouts) | the `[status]` line, `physics: ...` |

### Expected log sequence in device test 3 (up to the main menu)

`adb logcat -s McvsOffline mono mono-stdout` or LogFox filtered on the game. Lines 1-8 are as in device test 2 (request
numbers included), plus the physics milestones.

1. `[embedded] MCVS Offline 0.1.0-dev embedded host starting`, `[crash] hooks: ...`, `[boot] +0.0 s MCVS Offline host
   started by the game`, `[logfile] ... exists, N bytes before this run` (bootstrap.log, game.log, server.log), `[gamelog]
   Gameloft.Logs levels: ...`, `args.txt is up to date`, `[boot] offline server listening at http://127.0.0.1:51780/`,
   `[diag] [mirror] ...`, `[crm] CRM document built: 1864 offline_store items ...`, `[diag] [exit] how the last 5 game
   processes ended`.
2. **New, before #1:** `[boot] physics: client World #1: Havok multithreaded (GameOptions multithreadedPhysics true,
   settings embedded.physics.clientMultithreaded true; step gate on); engine threading: PhysicsComponent.DefaultDeferred True
   (client physics on a loading-pool thread), GameApplication.RenderThreadEnabled True, RenderThreadMode Full` (the values are
   whatever the phone's GameOptions profile says: True/Full if the S22 gets MidGPU through the `CORES>1` rule, False/None
   if it keeps the GPU defaults; nothing has shown which yet).
3. `#1 .../datacenters/mcvs/urls` -> `[boot] client Federation: eve config (#1)`, `[watchdog] game thread is tid N ''`, `#2
   locate/auth`, `[boot] native online SDK init requested (... call 1): skipped`, `#3 POST /authorize` -> `[boot] client
   anonymous session`, `#4 users/me`.
4. `#5 .../mcvs/urls` -> `[boot] second Federation: eve config (LocalAnubis)`, `#6`, `#7` -> `[boot] LocalAnubisManager.Start:
   super session`, `#8`.
5. `#9 verify`, `#10 locate/matchmaker`, `#11` -> `[boot] QuickLaunch: Finder session`, `#12` -> `[boot] QuickLaunch:
   lobbyconfigs ts`, `#13 encrypt_token`, `#14` -> `[boot] Controller login of the client`, `#15` -> `[boot] Controller
   'connect game': creating the TS World`.
6. **New:** `[boot] physics: server (headless) World #2: Havok single-threaded (GameOptions multithreadedPhysics true,
   settings embedded.physics.serverMultithreaded false; step gate on)`.
7. `#16` -> `[boot] third Federation: eve config (TS World)`, `#17`, `native online SDK init requested (... call 2): skipped`,
   `#18` -> `[boot] TS World: game-server session`, `#19`-`#22`, `#23 configs/users/me/game` -> `[boot] first CRM config ...
   N ms after the TS session`, `#24 encrypt_token`, `#25 verify` -> `[boot] TS lobby login: the TS World now loads
   Levels/Menus.level`. About 1.5 s of silence while the TS loads its level (normal).
8. `#26 POST /authorize` on behalf of the player -> `[boot] TS handshake: session on behalf of the player (#26)`, and on
   mono-stdout `[w2] ... FedRequestHandler.cs}.SendAsync:48 | Request .../authorize took N ms`. **This is where device test
   2 stopped.**
9. **The test:** `[w2] ... {RequestHandler.cs}.SendAsync:73 ... /authorize returned completed with code : OK`, then the rest
   of the TS handshake, roughly in this order: `#27 users/me`, `alerts/me` -> `[boot] alert stream open (the TS handshake
   reached the player's session)`, the first `profiles/...` read -> `[boot] TS handshake: first profile read (Seshat)` (a new
   player: the root is `{}` and the sections are created), `users?device_id=` -> `[boot] TS handshake: accounts for this
   device`, the section creations and the game-profile save -> `[boot] first profile write (Seshat)`, `location/?ip=` ->
   `[boot] TS handshake: geolocation lookup answered locally`, `games/mygame/alias` -> `[boot] TS handshake:
   games/mygame/alias`, `configs/users/me`. No `[watchdog] STALL` line, no `physics step gate: ... stepping without the gate`
   warning.
10. The client loads Menus.level (no requests), shows the language prompt and the age gate, then the main menu; `[boot] quiet:
    no request for 10 s ...` once the menu is idle is normal. Within a minute: `[status] boot: ...; watchdog: ... N beats; ...
    physics: 2 Havok worlds built (1 server Worlds made single-threaded), step gate on: N entries, M waited (max X ms), 0
    timeouts; native IAP library initialized: yes`.

**If it still hangs** at step 9: `[watchdog] STALL` at 5 s (now with the futex syscall), at 15 s the Java stacks and then the
`[threaddump]` sections: `native view: dumpable 0 (raised to 1 for the reads), /proc/self/mem open, ...` with one line per
thread, `native stack of <game tid> (raw)` / `(named)`, the same for the `Thread-22` siblings and the Finalizer, then `Mono
managed thread dump via libmonosgen-2.0.so: complete after N ms` and one `[threaddump] "<thread name>"` block per thread. The
game thread is the `"<unnamed thread>"` block whose frames contain `Gameloft.Engine.GameApplication:UpdateWorlds`. Its top
frames name the wait (a `(wrapper managed-to-native) ..._private` engine call, `Monitor.Enter`, `ManualResetEventSlim.Wait`,
...); see `/home/user/mcvs-work/reports/device2-threaddump.md` §5 for how to read it. Then one A/B run with
`"embedded": {"physics": {"serverMultithreaded": true, "stepGate": false}}` in settings.json should reproduce the
device-test-2 hang (and confirms the cause); a hang **with** the defaults is something else, which the dump names. If the
`[boot] physics:` line says `RenderThreadMode None`, `Render()` runs on the game thread and the render path is the first
suspect.

## Device test 1 (October 2026)

Samsung Galaxy S22 (SM-S901E, Snapdragon 8 Gen 1), Android 16 (SDK 36), patched APK at `eb50af6` (default patches), the
original game uninstalled first, the data pack downloaded again by the game's own installer. Two runs, logcat captured
with LogFox, plus one tombstone.

### What worked

The hook ran at +0.4 s, `args.txt is up to date`, the server bound 127.0.0.1:51780, the CRM document was built from the
player's data pack, and every request that reached the server was answered 200 in under 8 ms. The game's own Fatal log
lines reach logcat as `mono-stdout` (Gameloft.Logs' ConsoleLogger); the shipped `res/raw/args.txt` (`-LogCategoryFilter
Fatal`) mutes everything below Fatal.

### RUN 1, internet on: "loading" forever

Requests #1-#5 only: eve urls (client Federation, datacenter known from the saved `datacenter` file), locate auth,
authorize anonymous, users/me, then `GET config/<cid>/datacenters` on a new connection (the LocalAnubis Federation, no
`-DataCenter` in the args), answered 200 at 793.607 s, and then nothing for 20 s (no Fatal, no ANR). In RUN 2 the next
request (#6, eve urls of the same Federation) came 73 ms later.

* **Cause, verified part.** Every hop between two Federation requests is a `GameSynchronizationContext` continuation
  (no `ConfigureAwait(false)` on the request path), drained by the game thread once per frame in
  `GameApplication.UpdateWorlds`. So after #5 the game thread stopped draining its queue, and only with the network up.
  `OnlinePlatformComponent.Init_private` (posted to the game thread right after #1, for every World that knows its
  datacenter) is the managed entry of the native online SDK: `OnlinePlatformComponent_Init_private` posts the native
  `GaiaInit` event, whose handler starts the native "Gaia Thread" (Gaia::Update over 18 service slots, each under a
  pthread mutex, glwebtools/libcurl work) against hard-coded Gameloft hosts (`eve.gameloft.com:20001`, gameoptions,
  sink, ingameads, igp). It returned (the #2-#5 continuations ran after it), so it does not itself block.
* **Cause, inference (medium-high).** A game-thread call into the native SDK waits for a lock or a network timeout that
  the Gaia/curl threads hold while they talk to the real, half-dead hosts; in airplane mode DNS fails at once and
  nothing waits. The exact waiting call is not known (the release build has no logging of its own there); the next
  build's watchdog names it if the stall survives (see below).
* **Fix.** Patch `nativeonline` (default): `Init_private` calls the native SDK only when
  `McvsOffline.GameHooks.AllowNativeOnlineInit` says so, which answers `settings.json` `embedded.nativeOnlineSdk`
  (default **false**) and logs each request as a boot milestone. If our DLL cannot load, the native call is skipped.
  The managed args now carry `-DataCenter mcvs`, so no Federation asks eve for the datacenter list (#5 disappears, and
  a first run no longer goes through `SetDatacenter` -> a second `InitFederation`); `lobbyconfigs` `cmd_args` already
  carry it. `Federation.WaitForConfig` (no timeout when `GetConfig` fails) only feeds `Init_private`, so with the patch
  it can no longer hold anything up, and eve always answers 200.
* **What the native SDK did and what offline play loses.** Everything the game does with Federation is managed C#
  (report 09 §1). The singletons the per-frame native calls read are created by `MainLoop.Start`, not by GaiaInit (checked
  in libnativeengine.so: `IapStoreComponent_IsInitialized_private` reads `[0x16ff078]+0x40`, set at `MainLoop_Start`
  0x3d518c), so skipping GaiaInit dereferences nothing null. Lost, all dead offline anyway: native Gaia sessions
  (Janus, Seshat, Osiris, Hermes clients), ETS tracking (sink), ads (glads, ingameads), IGP cross-promotion and news, the
  Iris GameOptions download (the bundled `GameOptions_*.json` stay in use), native CRM point-cut popups. Not affected:
  crash reporting (Crashlytics, a separate path). **Open:** whether the native IAP library initialises offline, with or
  without GaiaInit; `IapStoreComponent` waits for it (no timeout) before it registers, and the unified shop, including the
  VersusCoins items, waits for that registration. The status line now reports it (`native IAP library initialized:
  yes/no`, also a boot milestone). The real-money shop cannot work offline in any case (empty CRM `iap`).

### RUN 2, airplane mode: crash when the TS World loads its level

Requests #5-#26 went through LocalAnubis, the QuickLaunch Finder, the Controller and the TS World's own Federation, CRM
`configs/users/me/game` and the TS lobby login; 1.7 s later the TS World's level load failed:
`Fatal: {World.Loading.cs}.ExecuteLoadLevel:399` with `NullReferenceException at Game.InputSettingsComponent.OnStart
[0x00011]`, rethrown by `MoreAsync.FireAndForgetTask` on the game thread, `UpdateWorlds:1048`,
`CurrentDomain_UnhandledException:852`, then Mono's `exit()` (the tombstone is the Adreno driver crashing in `exit()`:
noise, not a separate fault).

* **Cause (verified in code and IL).** The in-process TS/GS are headless Worlds built from the same SceneComponents.tx as
  the client. A component is created in a headless World unless it is `[KeepInHeadlessMode(false)]`; `InputSettingsComponent`
  has no attribute, and its `OnStart` dereferences `World.LocalStorage`, which is null in every headless World
  (`LocalStorageComponent` is `[KeepInHeadlessMode(false)]`). In this release build `Component.Events.GetSafeAction` is
  `ldarg.0; ret` (the server build's try/catch wrapper was stripped), so one exception in any lifecycle event of any
  server-World component ends the player's process. `ErrorHandler(Args.Headless)`, `-Headless` and `-Server` play no
  part (template diagnostics only; read once in the GameApplication constructor).
* **Fix: mandatory patches `headless` (Game.dll) and `headless-engine` (Gameloft.Engine.dll)**, every site verified against
  the 1.17.32 IL first:
  * `InputSettingsComponent` gets `[KeepInHeadlessMode(false)]` (it would also reset the player's process-wide input
    sensitivity and invert settings from a server World);
  * `GameplayComponent`: the four `OfType<HudGamepadComponent>().FirstOrDefault().BindingTo*Preset()` calls (OnStart on
    every GS map load, OnStop at teardown, the two pause handlers) are null-safe (HudGamepad is dropped headless);
  * `FlameZoneComponent.BroadcastEnd` and `BacktrackTimeDistortionExplosion.FadeOutEffect`: `GetComponent<VfxAnimatorComponent>().TriggerEnd()`
    null-safe (BroadcastEnd would throw, and send its RPC, every frame);
  * `SoundManagerGameComponent` and `SoundGroupSnapshotComponent`, `GameInputManagerComponent`: server Worlds no longer touch
    the player's VoxManager (OnStop stopped every sound group but six) or poll the gamepad a second time per frame;
  * the safety net: `GetSafeAction` returns `McvsHeadlessSafeAction.Invoke`, which calls the event directly for client
    Worlds (unchanged, fail-fast) and, for headless Worlds only, catches the exception and reports
    `[McvsOffline] headless world: <Type>.<Event> threw, the rest of it is skipped: <exception>` (logcat mono-stdout, first
    200) and to `GameHooks.HeadlessComponentException` (bootstrap.log, boot trace). Each such line names a component that
    needs a precise fix.

  The headless audit (all 70 TS scene components, Menus.level, every PvP map minus ClientSide, all 877 templates) found
  no other definite crasher in the TS; the GS ones are the GameplayComponent and Vfx sites above. Not changed:
  `ChangeGraphicQuality` on headless level loads and `FireAndForgetTask` for headless loads (low impact; reconsider if
  device logs show them).

### The 0-byte bootstrap.log and server.log

Both files existed with 0 bytes although logcat had every line. The old `RotatingFile` set a `failed` flag on its first
exception and never said anything again, and it kept the file open for the whole process: Android's MTP server refreshes
a file's size for a connected PC only on `IN_CLOSE_WRITE` (AOSP `MtpStorageManager`: `sendObjectInfoChanged` is sent on
CLOSE_WRITE only), so a PC that saw the file created kept showing, and copying, 0 bytes. Static analysis cannot tell
which of the two happened; the replacement settles both: `SafeLogFile` queues lines (no I/O on the caller's thread) and
writes each batch with open, seek, write, flush and **close**; every failure names its stage and goes straight to logcat;
after 3 failures it continues in internal storage; at start it logs `exists, N bytes before this run`.

### Diagnostics added for device test 2

| What | Where to find it |
|---|---|
| The game's own log at every level (`--game-log all`, default for now: `res/raw/args.txt` without `-LogCategoryFilter`) | logcat `mono-stdout`; copy in `logs/game.log` (settings `gameLog.mute`, default `profile:debug`) |
| Unhandled-exception reporter, installed first in `Bootstrap.Init` (AppDomain, AndroidEnvironment, unobserved tasks). Separate budgets: up to 3 fatal reports (the first always) and up to 5 non-fatal ones, so unobserved task faults can never use up the crash report | logcat `F McvsOffline: FATAL unhandled exception ...`; `logs/crash-*.txt` (fsync'ed); unobserved task faults: `E McvsOffline: NON-FATAL ...`, `logs/exception-*.txt` |
| Boot trace: one `[boot] +12.345 s` line per milestone of the boot chain (recognised from the requests), the native-init and headless-World hook calls, failed requests, a `quiet: no request for N s` line during boot | logcat `McvsOffline`, `logs/bootstrap.log` |
| Game-thread watchdog: one heartbeat a second through the game's work queue; on a stall of 5/15/60/180 s, the game thread's kernel state, wchan and syscall, the busiest threads, the Java stacks of the game and UI threads (at 15 s), the server's last request | logcat `W McvsOffline: [watchdog] STALL ...`, `logs/bootstrap.log` |
| Status line every 60 s for 10 minutes: boot trace, watchdog, every log file's size, bytes written, drops and last error, native IAP state | logcat `[status]`, `logs/bootstrap.log` |
| How the previous processes ended (`ApplicationExitInfo`, Android 11+; ANR traces and tombstones saved) | logcat `[exit]` 3 s after start, `logs/exit-*` |
| A phone-readable copy of bootstrap.log, server.log, game.log and the crash files (Android 11+) | Files app > Downloads > `MCVS-Offline` |

All of it uses only members of the game's trimmed assemblies (a Cecil test resolves the 170 type references, 751 member
references and 8,256 body operands of the built DLL against `MCVS_REFS`, none missing, no linker stubs), never throws
into the game, and does no I/O on the game or UI thread beyond what the host did before.

### Expected log sequence in device test 2

Superseded by the device-test-3 sequence above (steps 1-8 are what device test 2 showed).

## 1. Implemented

| Area | What | Code | Protocol note |
|---|---|---|---|
| Core | HTTP/1.1 server on raw sockets (keep-alive, chunked, `Expect: 100-continue`, streams), router with re-dispatch, MiniJson, accounts and self-describing tokens, atomic writes with file and directory fsync, settings with merge-overrides, request/unknown-endpoint logging, GCBF/LZ4 data-pack reader | `server/Core` | [MODULES.md](MODULES.md) |
| AUTH | eve, pandora (locate, service-prefix aliases), authorize (anonymous, game server, on-behalf, transfer code), users, compliance, aliases, credential links, `encrypt_token`/`verify`, `IAuthenticator` | `server/Services/{Eve,Pandora,Auth}` | [auth.md](protocol/auth.md) |
| STORAGE | Seshat profiles with verbatim values, nested `include_fields`, deep merge, no ETag, 401-never-403, arrival-order lock, cache, backups, corrupt-file recovery (503, never "missing"), batch/backups/visibility/data endpoints, `IProfileAccess`, LAN seam | `server/Services/Storage` | [storage.md](protocol/storage.md) |
| CONTENT | CRM over the player's own preconfiguration (validated against every client cast), matchmaker `lobbyconfigs`, unlock coupons, local Iris assets, generated VersusCoins economy (1,864 entries) | `server/Services/{Crm,Matchmaker,Assets}`, `server/Content`, `tools/content-gen` | [content.md](protocol/content.md), [ECONOMY.md](ECONOMY.md) |
| MISC | Hermes mailboxes, Kairos alert stream, Olympus leaderboards with league groups, Osiris events/profiles, clans, wall, group chat, profanity, game_stats, BanTool, geolocation, tracking/marketing catch-alls | `server/Services/{Social,Messaging,Alerts,...,Misc}` | [misc.md](protocol/misc.md) |
| LIVE OPS | Weekly division seasons (17 division events, 100-entry season boards with rivals that grow and freeze), daily missions, weekly events, weekly modifier as CRM table overrides, CRM `expiry` for the client's refresh, culture-safe dates | `server/Services/Social/LiveOps`, `server/Services/Leaderboards/SeasonRivals.cs`, `tools/content-gen/liveops_tuning.json` | [LIVEOPS.md](LIVEOPS.md), [misc.md](protocol/misc.md) |
| Unlocks | `settings.unlockAll` through the inventory ledger (starts only once the game profile and the inventory section are stored, waits for the player's alert stream, backup, ordered batches with the merc unlock re-checked before each, `set_profile` alerts) | `server/Services/Unlocks` | [unlocks.md](protocol/unlocks.md) |
| EMBED | In-game bootstrap (game root via the game's own Java helpers, `args.txt` managed block with `-DataCenter`, device id, logcat, server thread); diagnostics (crash reporter, self-reporting log files, boot trace, game-thread watchdog with managed and native thread dump, game-log bridge, phone-readable copy, exit reasons); patcher (hook, headless, headless-engine, physics, nativeonline, waits, geo, bottile, finder, lan; `res/raw/args.txt` for `--game-log`; vendor-wide manifest permission renamed into the package namespace; AOT strip; ABI selection (every ABI by default, `--abi arm64-v8a` for 64-bit only); signatures (build 7); debloat (build 8: 44 dead manifest elements, Crashlytics/Fabric files, SDK start-up calls); raw zip rewrite; v1/v2/v3 signing); installer-data restore (saved CDN index, journal and preferences, size-only index for a complete `Data/`, background CRC pass, installer monitor) | `hosts/embedded`, `patcher` | [PLAYER-SETUP.md](PLAYER-SETUP.md), [INSTALL-OFFLINE.md](INSTALL-OFFLINE.md) |
| Hosts | Standalone net8 console host; embedded DLL compiled against the game's trimmed Mono BCL (ILVerify-clean) | `hosts/standalone`, `server/McvsOffline.Server.Embedded.csproj` | [DEV-LOOP.md](DEV-LOOP.md) |

### How it was checked

`MCVS_REFS=<apk assemblies> MCVS_REQUIRE_EMBEDDED=1 MCVS_REQUIRE_REAL_CLIENT=1 scripts/build.sh`: net8 solution, embedded
build against the game's own BCL, then all tests. Result after the device-test-2 fixes and their review: **McvsOffline.Tests
485/485, McvsOffline.Embed.Tests 119/119, 0 skipped**:

| Tests | Count |
|---|---|
| Core (HTTP, router, JSON, accounts, settings, storage helpers, data pack, embedded-DLL references, libc SO_REUSEADDR, log files, boot trace) | 145 |
| AUTH | 64 |
| STORAGE | 83 |
| CONTENT (including the generator's own Python tests and the real data pack) | 73 |
| MISC | 110 |
| Unlocks | 8 |
| End-to-end conformance (real client) | 2 |
| Embedded host (including the crash reporter and its separate fatal/non-fatal budgets, the watchdog, the thread dump, the physics hooks and step gate, a Cecil resolution of every reference of the built DLL against the game's assemblies, and every P/Invoke typed for both shipped ABIs: no 32-bit `off_t`/`ssize_t` mismatch on armeabi-v7a) | 65 |
| Patcher (including the binary manifest rewrite, `res/raw/args.txt`, the device-1 and device-2 patches applied to the real Game.dll and Gameloft.Engine.dll, then executed and JIT-compiled on net8, and the IL fingerprints of the verified methods) | 54 |

**Real-client conformance** (the player's Gameloft.OnlinePlatform, Gameloft.Engine, Game, Gameloft.Templates and
Gameloft.Util loaded from `MCVS_REFS` into net8; mandatory in `build.sh` when the folder has them). Flows that ran
through the game's own code against the complete server, over HTTP:

- **Boot** (`Conformance/EndToEndConformanceTests`): eve `GetConfig` (status None, 7 urls); `LocateService` for 18
  services; anonymous `OpenSession`; `GetAccountInfo`; game-server `OpenSession` and on-behalf `OpenSessionForUser`;
  `GetAccountsForDeviceId` (`[]`); `CreateAlias`/`LookupAlias`; `AddCompliances`; CRM `GetCrmConfig("game")` on the GS
  session (`IsDevelopment=false`, `Matchmaking 1/0`, `RedeemCoupons=1`); `Crm.GetStandardConfiguration` +
  `StandardCrm.Data` (every client parser: 1,864 `OfflineStoreData` items, `IapStoreData`, point-cuts, fatigue
  groups, events, ads, game parameters) and the `iap` selector; R1 `GameProfileSection.Load` (new profile, null
  template); R2/R3 `ProfileSection.LoadSections` of the five TS sections; R4 `GameProfileSection.Save` with a dump
  written by the game's own Templates writer from the game's `DefaultProfile.tx`, then again at 1.4 MB; R5
  `SaveSections`; R11 language; R8/R9 currentMatch with the GS's own token; `EncryptedAccessToken` → `Verify` →
  `RoomUser.FromAccountInfo` on an HTTPS-forced LocalAnubis Federation; `lobbyconfigs` ts/gs through LocalAnubis
  `GameConfig.FindGame`, `Controller.GetConfigArgs` and `GameApplication.Arguments.FromString`; coupon
  `AssetsService.RedeemCoupon` → `Json.LoadArray` → `RewardHelper.CreateRewardItem`; **server restart** with a new
  client: same account and alias, R1 byte-exact at 1.4 MB, read back identically by the game's `TemplateReader`,
  sections persisted; `GetReadOnlySessionForUser` reading the player's profile from another game-server session.
  80 requests, all 200 on known routes.
- **unlockAll**: the grant starts after the real `LoadSections` (R3 creations) and `GameProfileSection.Save` (R4),
  once the real `AlertListener` is open (the granter's own readiness signal, checked); 11,334 ledger keys read by the
  TS's `IapStoreComponent.InventorySection`, all JValue integers in order, 4 `set_profile` alerts received by the real
  `AlertListener`, every key accepted by `RewardItem.Parse`.
- **AUTH** (`Auth/RealClientConformanceTests`): anonymous, game-server, on-behalf and LocalAnubis sessions; first-run
  calls; transfer codes; recovery through the client's own 401 handling after the server lost its tokens and accounts.
- **MISC** (`Misc/ClientConformanceTests`, now against the real AUTH): Hermes gifts and deletion, the Kairos stream,
  `LeaderboardData`, `SocialEvent`, social profiles, clans and the clan wall, the chat room stream, profanity,
  game_stats; the time-limited-event validator agrees with the game's `ParseTemplate` on 36 templates.
- **Embed**: our `args.txt` block through the native flattening and the game's real `GameApplication.Arguments`
  parser (`Embedded` / `QuickLaunch` / URL / super credential / device id / LAN host); the `geo` patch applied to the
  real Gameloft.Util and its `GetRegion` answered by our server; patches checked on the real APK (dry run); the real
  manifest round-trips byte for byte through the patcher's binary-XML code, and after the permission rename
  `aapt2 dump xmltree` is the original dump with exactly the three `glshare` uses renamed.

Also checked by hand: the standalone host with curl (boot sequence, byte-exact storage, CRM, `lobbyconfigs`,
lobby hand-off, coupons, geo, alerts with a pushed gift, unknown-endpoint logging, `unlockAll` after a restart, and a
new player's first load replayed request by request: R1, R2, the four R3 creations, R4, the alert stream, then a late
R3 `inventory` reset mid-grant); the patcher on the real APK: `apksigner verify`, `zipalign -c`, `aapt2 dump xmltree`
(every declared permission package-scoped) and `aapt2 dump badging` of the output; ILVerify on the embedded DLL
against the game's BCL (no findings).

### Review fixes (October 2026)

| Finding | Fix | Tests |
|---|---|---|
| unlockAll started at the first write under `_mcvs` (a new player's first R3), so the R3 `inventory` Set could erase `UnlockMerc_ALL_MAX` after it was written and the merc-dependent keys became alternate rewards | Trigger on the game-profile section Sets only, and grant only when the stored profile holds the game profile and the inventory section; not-ready accounts are retried at the next save; the ledger is re-read before each batch and the merc unlock rewritten first if it is gone (and heads every merge); the first batch waits for the player's alert stream; the backup now holds the game profile | `Unlocks/UnlockGranterTests` (first-load sequence, mid-grant reset, stream wait, selectors), conformance |
| The re-signed APK redeclared the vendor-wide signature permission `glshare.permission.ACCESS_SHARED_DATA`: `INSTALL_FAILED_DUPLICATE_PERMISSION` next to any official Gameloft game | `patcher/Manifest`: the pool string is renamed to `<package>.mcvs.glshare.permission.ACCESS_SHARED_DATA` (permission, uses-permission and the KeyProvider's `android:permission` together); report 08 §L corrected | `Patcher/ManifestPatchTests`, pipeline and real-APK tests |
| A `settings.json` with a syntax error was replaced by the defaults when the embedded host added its keys | `Settings.IsFallback`; `Save()` refuses for it; the host skips its save; an unreadable file is never written back | `SettingsTests`, `EmbeddedHostTests` |
| Mono's `SetSocketOption(ReuseAddress)` also sets SO_REUSEPORT, so a same-UID second listener could share 51780 | The embedded listener sets SO_REUSEADDR alone through libc (`SocketReuse`) | `SocketReuseTests`, `EmbeddedAssemblyTests` (libc imports) |

What the harness cannot show: Mono's HTTP stack (the client ran on net8's `SocketsHttpHandler`), anything native
(Gaia, JNI, the engine's GameObjects), the Worlds themselves (no TS/GS World ran), timing on a phone. Device test 1 covered
the first part of the boot on a real phone (see above).

## 2. Known gaps (by design or not yet built)

- **LAN (M3)** is experimental: guests must use the host's server for everything (topology A). Tokens issued by a
  guest's own server are unknown to the host (401, the log names the issuer); storage, mailboxes and alerts are not
  forwarded between devices (`IProfileStoreLocator` is the seam).
- **Live ops** use our own tuning: the original season length and rival strength are not in the client
  ([LIVEOPS.md](LIVEOPS.md)); permanent achievements are switched off in 1.17.32 itself; Arena-mode events and shard
  "mega events" are not generated. Changing the live-ops content while an event runs resets that event's progress.
- The CRM `iap` catalogue is empty on purpose (it would start real Google Play purchases nothing can fulfil).
- A brand-new account's root GET is `{}` without the server-maintained fields (`credential`, `datacenter`,
  `created`) until its first save; every game reader defaults them (checked in the conformance harness).
- Scopes are recorded but not enforced; `auth.requireSuperPassword` is off by default (any `game:` credential may act
  on behalf of players).
- Native SDK calls (global device id, ads, ecommerce) have unknown formats: they reach the unknown-endpoint log.
- The DLC download path (inactive, `UseDlc=false`) cannot work with our `http://` locate answers.
- `tools/cdn-mirror` (mirror, verify, offline install) is in the repository; a separate data-pack decoder tool
  (`tools/datapack`, research report 11) is not, the server's own data-pack reader is in `server/Core/Content`.
- A rebuilt installer index (a `Data/` folder without Gameloft's index) checks sizes only; damaged content of the right
  size is accepted (run `tools/cdn-mirror/verify.py` on such a backup, or add a mirror's `pack.info`).
- Internet P2P (M4) is not started; of the preservation extras (M5) the CDN mirror and offline install exist.

## 3. Device-test checklist (priority order)

Run the dev loop first ([DEV-LOOP.md](DEV-LOOP.md)): it needs no patch, and the server log on the PC shows every
request. Then the player build ([PLAYER-SETUP.md](PLAYER-SETUP.md)). Sources: the `remaining_risks` of
[verify-C1](research/verify-C1-args-redirect.md) to [verify-C7](research/verify-C7-integrity-patching.md) and the
modules' open issues.

### P0: does it boot to the menu

1. **args.txt is read** (dev loop). `adb push` to `Android/data/<pkg>/files/` works and the game reads a shell-owned
   file on Android 11-15; the native VFS opens the absolute path; the first `GET /config/.../datacenters` reaches the
   PC (verify-C1 §5). If nothing arrives: BOM, tabs, `Embedded` spelling, `SDFolder` on another volume.
2. **The embedded server starts** (player build). Confirmed on the S22 in device test 1 (hook at +0.4 s, server on
   127.0.0.1:51780, CRM built, args.txt block in place). Still to see: `[logfile] ... exists, N bytes` with N > 0 and the
   copies in Download/MCVS-Offline; a quick kill-and-restart binds 51780 again (no "SO_REUSEADDR not set" warning).
3. **The TS World loads within the timeouts.** Device test 1 (RUN 2): the TS reached its level load 3 s after the first
   request and failed 1.7 s later (the headless crash, fixed). Device test 2: the level load works, no headless-world line;
   the game thread then hung in the TS handshake frame (the physics race, fixed in build 3). Check the device-test-3
   sequence above from step 8 on: `RequestHandler.SendAsync:73` after #26, the rest of the handshake, the menu, no
   `[watchdog] STALL`. With the internet on, steps 3-7 must follow without a `[boot] quiet` line (true in device test 2).
4. **The 1 s CRM window.** The TS's first `GET /configs/users/me/game` must be answered within 1 s of the TS starting,
   or `IsDevelopment=true` leaks into that session (verify-C5). The document is pre-built at startup; compare the
   request's timestamp with the server's "CRM document built" line on a slow phone. Device test 1: 229 ms after the TS
   session (the boot trace now prints this and warns above 1000 ms).
5. **Mono transport.** `Expect: 100-continue` on the large R4 save; `fed_id%3A` vs `fed_id:` in paths (both accepted;
   the log shows which); the chunked `text/event-stream` read line by line (`GET /alerts/me` stays open); no HTTPS
   anywhere (all our URLs are `http://` with an explicit port) (verify-C3 §8, MISC notes).
6. **Native IAP and online SDK.** The native online SDK (GaiaInit) is off by default now (`nativeonline`; turn it back on
   with `embedded.nativeOnlineSdk: true` to compare). Device test 2: `native IAP library initialized: yes` without
   GaiaInit. Still to see: the shop and the VersusCoins tab once the menu is reached. With "no", `IapStoreComponent` never registers and the unified shop (VersusCoins items included) cannot load: open the
   shop and the VersusCoins tab. If it hangs, the fallbacks are the report 04 §14.5 patch (raise LibInitialized and
   StoreRefreshed with an empty store) or `crm.iap=preconfig`. Also look for `service-prefixed path`, `locate of unknown
   service` and unknown endpoints such as `/users/me/approvals/` (verify-C2 §4.4-4.5, AUTH and CONTENT notes).
7. **First-boot gates.** Language prompt and age gate (`crm.skipAgeGate`); after the forced uninstall of the original
   game, the Java installer's data validation: build 5 restores or rebuilds its index from data on the phone
   ([INSTALL-OFFLINE.md](INSTALL-OFFLINE.md)); `qaTestingConfigs.txt SKIP_VALIDATION=1` stays the untested last resort
   (report 10 §4.3) (verify-C2 §4.6-4.7, EMBED notes).
8. **No 4xx on the first-install load path**, in particular none on `profiles/...` (a 403/404 there resets the profile).

### P1: progression is kept

9. **Save and reload.** Play, wait for the 5-minute autosave or a match end (R4 then R5 `200` in the log), kill the
   app, start again: same profile. `data/storage/profiles/<account>.json` grows; backups appear every 30 minutes.
10. **Match end on the GS** saves the result; the bot-match resume race (verify-C3 §4): a match result followed
    quickly by a resume GET must not restore the pre-match state.
11. **Bot matches.** With the `bottile` patch the BOTMATCH tile goes through the GS flow and pays full rewards
    (trophies, MVP, merc XP) once PvP is activated; quick PvP starts at once and is filled with 7 bots (`instantBots`)
    (verify-C4).
12. **The GS's Lobby round-trips** (`encrypt_token`/`verify` for its own super session, `GetRoomInfo`) never fail: a
    failure there plays the match and grants nothing (verify-C4 §4.1). Fed ids must match on both paths (§4.3).
13. **Matchmaking after a network change** (Wi-Fi or airplane mode toggled while the game runs), fixed by the default
    `finder` patch; drop it with `--patch nativeonline,geo,bottile` if it misbehaves (verify-C6 §2.2, EMBED notes).
14. **Divisions and leaderboards.** `SelfData` gives a rank in a 100-entry league group; the season end promotes or
    demotes once and the reward reaches the inbox; the rivals' pace is plausible against real trophy gains (build 5:
    "What to check in device test 5", [LIVEOPS.md](LIVEOPS.md)).

### P2: everything obtainable

15. **unlockAll.** On a brand-new player the log shows `waiting: no game profile saved yet` for the R3s, then the
    grant after R4 once the alert stream is open (no "no alert stream" warning); the TS receives `set_profile` on the
    player's alert stream and claims each batch, the mercs first; reward popups with 400 keys are acceptable; check
    whether the GS also diffs and double-grants ([unlocks.md](protocol/unlocks.md)).
16. **Coupons.** "Redeem code" shows under Settings → Account (`RedeemCoupons=1`); `UNLOCKALL` gives one inbox
    message of about 11.3k rewards (UI and save size untested; the split codes `UNLOCKMERCS`, `UNLOCKCAMOS`, ... are
    the fallback) (verify-C5 (b), CONTENT notes).
17. **The VersusCoins hub tab** renders the reward types we sell there (weapons, gear slots, mod tier 4, crates, VIP,
    packs) and pack names localise (verify-C5, CONTENT notes).
18. **Economy multipliers** sent as `*x` cells come back as integers in the game's typed tables;
    `DailyShop_Cosmetics.Type` really is the price currency (verify-C5).
19. **Developer menu** (`devMenu: true`): the 6-tap gesture opens `menu_cheats`; the TS sees `127.0.0.1` as IPv4 so
    the handshake reports a developer (verify-C5).
20. **Inbox.** A gift posted to the secured mailbox shows up and can be claimed and deleted; after a forced kill the
    `multipleConnectionDetected` notice does not kick the player (MISC notes).

### P3: performance and LAN

21. Startup and first-use time with JIT instead of the deleted AOT images (`--patch none` keeps the most AOT code);
    the cold 1.4 MB profile read (about 200 ms on a desktop) on the phone (EMBED notes, verify-C7 §8).
22. LAN (experimental): `gethostname()`/interface order and the `PublicHost` the `lan` patch picks; UDP 50034
    broadcasts without a MulticastLock; inbound TCP/UDP to a foreground host under Doze; CPU/RAM of one TS World per
    guest; template-injected Finder host (verify-C6 §7).
