# Protocol notes: CONTENT

Owner group: **CONTENT** (Crm, Matchmaker, Assets, server/Content, tools/content-gen).
Scope: CRM configuration, matchmaker lobby configs and rooms, Iris assets and coupons, generated content.
Evidence: ARCHITECTURE §2.2, §3 'CRM' and 'Economy / unlocks'; verify-C2 §1.3-1.7, §3; verify-C4 §3-4; verify-C5; live ops: ../LIVEOPS.md;
verify-C6 §2.6, §5; report 04 §4-6, §10-11, §15; report 06 §4.5-4.8; report 10 §5. Economy design: [../ECONOMY.md](../ECONOMY.md).

## Endpoints

| Method | Path template | Route name | Response | Evidence | Status |
|---|---|---|---|---|---|
| GET | `configs/users/me` | `crm.config` | 200, complete CRM document (below) | verify-C2 §1.7, report 04 §5 | implemented |
| GET | `configs/users/me/{*selector}` | `crm.config.selector` | 200, JSON of the sub-tree (`game`, `game.CrashlyticsParams.Enable`, `iap`; `/` also separates); 404 `{"error"}` if absent | report 04 §5.1, `EN/CRMServerSettingsComponent.cs:100-150`, `EN/ServerComponent.cs:365` | implemented |
| GET | `configs/users/{user}[/{*selector}]` | `crm.config.user[.selector]` | same document (the game only uses `me`) | Hestia shape | implemented |
| GET | `lobbyconfigs/{clientId}/{serverType}` | `matchmaker.lobbyconfigs` | 200 `{"game":{...,"cmd_args"},"automatchers":{...}}` | verify-C2 §1.3-1.4, report 06 §4.8 | implemented |
| GET | `rooms/{clientId}` | `matchmaker.rooms.find` | 200 `[]` + warning (rooms live in the local Finder) | `EN/ClientLink.cs:377-440` | conservative |
| POST | `rooms/{clientId}/quick_launch`, `rooms/{clientId}/automatch/{automatcher}` | `matchmaker.rooms.*` | 404 `{"error"}` + warning: only reached outside Embedded mode | `EN/ClientSettings.cs:341-351` OverrideAnubisHost | conservative |
| GET | `coupons/{clientId}/{code}` | `assets.coupon` | 200 `[{"type":"<rewardId>","quantity":<int>}, ...]`; 404 = "coupon does not exist"; the page code (`admin.redeemCode`) is always 404 and may open the settings page (below) | verify-C5 (b), `EN/AssetsComponent.cs:180-210` | implemented |
| GET | `assets/{clientId}` | `assets.list` | 200 `[{name, hash (sha256), size}]` of `<data>/assets` (normally `[]`) | report 10 §5 | implemented |
| GET | `assets/{clientId}/{name}` (+ `/metadata[/{attr}]`, `/url`) | `assets.get` | the local file (Range → 206/416), metadata JSON, URL text; 404 when not provided | report 04 §11 | implemented |
| GET | `_mcvs/crm` / POST `_mcvs/crm/reload` | `crm.debug` / `crm.reload` | diagnostics: preconfiguration source, size, `_ServerConfig.default`, validation problems, the `collector` block (below) / rebuild | ours | implemented |
| GET | `_mcvs/collector?account=<id>` | `crm.collector` | what the Cosmetic crate read from that player's save (below); 400 without `account` | ours | implemented |

All of them answer without an access token (assets/coupons never carry one; CRM and lobbyconfigs do, but the answer
does not depend on the user). No `ETag` is ever sent, so the client never sends `If-Match` back.

## CRM document

Built by `CrmDocumentBuilder`, layers in order (last wins):

1. The player's **`Config/crmpreconfiguration.json`** from their own data pack (`Data/b/de76cd6.f`, GCBF/LZ4, name =
   crc32 of the logical path; decoded with the Core `DataPack`, byte-identical to `tools/datapack/gcbf.py`). Lookup:
   the host's data pack, `crm.dataPackDir`, then `<data>/../Data` (embedded layout). While it is missing (fresh install:
   our server starts before the Java installer downloaded `Data/`) it is retried every 10 s; a corrupt file is ignored
   until restart. At load we drop the ~175 KB of empty `_OverrideTables` changers and the iap catalogues we do not use.
   Without it the document is built from our content alone and is still complete.
2. **`crm_overlay.json`**: `13yolimitationpopup.enabled=false`, `ReviewPopupParams.Ignore=true`,
   `CrashlyticsParams.Enable=false`, `_OverrideTables.MCVS_System.GameFeatures["Id@Enabled"]["RedeemCoupons"]="1"`,
   `_ServerConfig.default` with `IsDevelopment:false`, `DisableServerTracking*:true`, `UnsupportedLinkFrom/To:[]`;
   top-level `tags: []`.
3. **`offline_store`** = the generated catalogue (`crm.offlineStore`: `generated` | `merge` | `preconfig`), prices ×
   `economy.priceMultiplier`. Entries the client could not parse are dropped and logged; `promotion: null` is removed.
4. **`iap`** = `{"prices":[]}` (`crm.iap=empty`, default) or the preconfiguration catalogue of `crm.iapLanguage`
   (`crm.iap=preconfig`; the other languages are dropped when the preconfiguration is loaded, so switching this
   setting needs a restart). It must exist: the client IapStoreComponent only registers after the native lib called
   back for the CRM `iap`, and `CrmConfig.IapStore.Items` is read without null checks.
5. **Settings**: `game._ServerConfig.default.IsDevelopment = devMenu` (JSON bool, explicitly false by default:
   loopback clients are always in the developer edge list, verify-C5 (a)); `AdditionalDeveloperIpRange` =
   `crm.developerIpRange` when set; `Matchmaking = {MinPlayers:1, MaxOpenTimeInSeconds:0}` when `instantBots`, merged
   with `crm.matchmaking`; `game.AgeGateParams.Ignore = crm.skipAgeGate` (true: no age gate on first boot);
   `crm.redeemCoupons=false` removes the RedeemCoupons cell; economy multipliers become `_OverrideTables` MUL cells
   (`economy.json` targets, see ECONOMY.md); `crm.eventIds` → `groupware.events.participation`.
5b. **Live ops** ([../LIVEOPS.md](../LIVEOPS.md), from `LiveOpsService.Snapshot(now)`): the current and next season's 17
   division event ids, today's and tomorrow's daily missions, the season's weekly event (and the next one during the
   last day) appended to `groupware.events.participation` (after `crm.eventIds`); top-level **`expiry`** = the next
   schedule step as `"yyyy-MM-dd HH:mm:ssZ"` (the TS refetches CRM, and with it the events, when it passes; always ≥ 2 s
   ahead); the season's modifier cells and the `liveops.seasonRewardMultiplier` cells as MUL cells. A MUL cell that
   is already there (economy multiplier) is multiplied, never overwritten; a SET/ADD cell is left alone.
5c. **The Cosmetic crate** ([../ECONOMY.md](../ECONOMY.md) §5, [../design/economy-collector.md](../design/economy-collector.md)
   §2): SET cells written by `CollectorPlan` after the live-ops cells and before the overlay (an overlay cell on the same
   key wins), by phase (`CollectorRuntime.Status`): **Active** (collector on, live ops on, valid `collector.json`, `store` patch not
   known missing, now ≥ `since`): the static layer, 510 cells: `LootCrates` (`Crate_Event_Special`: `Id@LocId`
   `crm.CrateCustomization`, `Id@CratesCount` = `itemsPerCrate`, `Id@RewardChance#2` 0; the 14 `Crate_<Tier>_4To10Place`
   / `_11To20Place` crates retargeted to its content table with `band × itemsPerCrate` items), the content table
   `Crate_Event_Special` rows 1–39 (`Id@Type`, `Id@Subtype`, `Id@Subtype2`, `Id@Weight#1`; carriers 4–7 with
   `Id@BundledRewards` + `Id@AlwaysGiveBundledRewards`), the season emblem bundles of the 21 placement tables, and
   `ID@Unlockable` cells of `KillsignatureAvatars` (`true` for the crate's locked avatars, `false` for the Kayac ones)
   and `KillsignatureBackgrounds`. **Guard** (from a day before `since`, or for good once live ops served the crate,
   or collector off / content missing / store patch missing / a build or validation failure): 5 cells, one row of 70
   VersusCoins. **Dormant** (before that): nothing; the document is byte-identical to the one before the collector.
   `MinimalDocument` always carries the guard.
6. `crm.overlay` from settings.json, deep-merged.
6b. **Inbox item** (`newsfeeds`, build 9; [../design/settings-page.md](../design/settings-page.md) §9.1): one entry
   `{"news_id": "mcvsoffline_FromMCVS", "title": "MCVS Offline", "body": ..., "redirect": "http://127.0.0.1:<port>/_mcvs/ui/#news",
   "asset": "", "youtube": "", "timestamp": <Monday 00:00 UTC of this week>}` appended to an overlay's `newsfeeds`
   (replacing an entry with the same `news_id`). The inbox shows it as the message "MCVS Offline" from "MCVS"; its button
   opens the page through the `adminlink` patch. `redirect` is a fixed marker, **never a secret**: `configs/users/me`
   answers anyone. With `crm.newsfeedLink` false, `admin.enabled` false or no admin module the list is `"newsfeeds": []`
   (never absent: the inbox removes an old message only when the key is there). `CrmDocumentValidator.Sanitize` keeps
   `newsfeeds` an array of objects with string members and an integer `timestamp`, and drops entries without `news_id`.
7. `game._OverrideTables` is pruned (empty changers, keys without exactly one `@`, non-scalar cells) and **mirrored**
   into `_ServerConfig.default._OverrideTables`: the TS applies the former, the GS applies the latter and forwards it to
   clients in its handshake (report 04 §4.2, `GM/GameplayCRMComponent.cs:119-203`).
8. Ad pointcut actions (`show_ad*`) of the preconfiguration are dropped (`crm.keepAdPointcuts`), `crm.pointcutActions`
   appended after validation.

9. **Per player, full document only** (`configs/users/me` with a player's token; `crm.omitOwnedSignatures`, default
   true): the `offline_store` entries of the kill-signature avatars and backgrounds the player owns are left out
   (`CrmOwnedStoreItems`): the signature screen lists owned ones from the profile, not the store
   (`GM/GameOfflineStore.cs` GetKillSignItems). The owned set is read from the stored `_mcvs.gameProfile` dump
   (`KillSignatureAvatars`/`KillSignatureBackgrounds` id arrays) and taken again only after that dump was read
   (`storage.gameprofile.read`: the TS's login and `ReloadProfileAsync`), so the refetch after each save gets the same
   text. An unexpected dump shape serves the whole store. Selectors and explicit user paths are never filtered.
   While the Cosmetic crate is **Active**, the same path also runs with `crm.omitOwnedSignatures` off, and once the
   player's save has been read it patches the player's cells (`PlayerInventory` scans the avatars, backgrounds,
   sounds and `Mercs`; an unreadable part falls back to the static assumption for that part only): the 39
   `Id@Weight#1` cells by what the player still lacks, the `Unlockable` cells for the unowned ids only, and the three
   emblem bundles (the first unowned emblem of each star count). **Mirror policy**: the static document's
   `_ServerConfig.default._OverrideTables` (the `game` selector the TS/GS install) carries the layer without the
   `Unlockable` `true` cells; the per-player document's mirror carries only the guard. Server Worlds log in with the
   `game:` super credential and always get the static document.

The result is checked by `CrmDocumentValidator` against every hard cast and unchecked dereference of the client
parsers (`OP/StandardCrm.cs`, `OP/OfflineStore*.cs`, `OP/Iap*.cs`, `OP/PointCut*.cs`, `EN/CRMTableChanger.cs`); problems
are logged and listed by `GET /_mcvs/crm`.

**Timing.** The TS waits only 1 s for `configs/users/me/game` on its game-server session (`WaitForCRM`) before it keeps
its defaults (IsDevelopment=true); the GS waits forever for a 200 (`GetGameParametersAsync` has no fallback). So the
document is built on a warm-up thread at startup, selector requests wait at most `crm.selectorWaitMs` (300 ms) for the
preconfiguration, full-document requests `crm.documentWaitMs` (5 s), and any exception still yields a minimal valid
document (never a 5xx). The document is cached and rebuilt when the effective settings change.

**`GET _mcvs/crm` → `collector`**: `phase` (what the document carries), `decided`, `reason`, `since`, `guardSince`,
`failure` (a build failure that fell back to the guard), `cells` (`game`, `mirror`, `playerMirror`), `enabled`,
`itemsPerCrate`, `content` (`collector.json <hash>` or the problem), `storePatch` (true / false / null = unknown),
`cratesServed` (the latch, `<data>/collector-guard.json`, or null) and `staticWeights`. The admin status (`GET
_mcvs/api/v1/status`, `crm.collectorPhase`, `crm.collectorReason`) and the page's Status screen show the phase.

**`GET _mcvs/collector?account=<id>`** (`fed_id:` prefix allowed): `phase`, `reason`, `reads` (profile reads since
start), `source`, `dumpChars` (or `dump: "missing"`), `parts` (`avatars`, `backgrounds`, `sounds`, `mercs`: `known`,
`owned` / `agents`, `problem`), `agents` (`id`, `unlocked`, `skins`, `camos`, `jerseys` counts), and while Active
`left`, `leftByCategory`, `unlockableCells` and the player's `weights`.

**Cross-module.** `CrmDocumentCache` is registered in `context.Features` (other modules may read the current document;
no events are published). The cache key includes `LiveOpsSnapshot.Key` (settings, content hash, season, day,
announcement, modifier) and the inbox item (week, marker URL, on/off), so the document is rebuilt once per schedule step
(or when the admin module's portal appears or the port changes), never per request. The admin module registers after
the CRM module, so while `crm.newsfeedLink` is on the warm-up waits up to 2 s for `IAdminPortal` before it builds;
requests never wait for it.

## `collector.json`

Generated by `tools/content-gen` (`build_collector`, from `collector_tuning.json` and the catalogue), listed in
`manifest.json`, read by `CollectorRuntime` (overridable in `<data>/content/`, re-read when it changes). Ids only:
`crate` (`id`/`table` `Crate_Event_Special`, `locId` `crm.CrateCustomization`, `image`), the 39 `rows` (`id`,
`category`, `type`, `subtype`, `subtype2`, `bundle`, `item`, `merc`), the pools as id ranges (`avatars`: `base`,
`drawable`, `unlock`, `lock`, `kayac`, `reserved`; `backgrounds`; `sounds`; per-agent `camos`; `jerseys`), `season`
(tiers, division → tier, the crates to retarget, the placement tables, the 12 × 4 emblems), `levels` and a `summary`
(596 no-source items, 665 in the base tier, 9,365 paid pairs). A file that is missing, broken or fails the
preview-safety rule (no sound row with weight; sounds only as bundles on random-background rows) switches the crate to
the guard and is shown in `GET _mcvs/crm`. Schema: [../design/economy-collector.md](../design/economy-collector.md) §3.4.

## Matchmaker lobbyconfigs

`GameConfig.FindGame` (LocalAnubis Finder and Controller) reads this; the Controller swaps `GameApplication.Args` for the
parsed `cmd_args` while the in-process World is built, so they must carry what that World reads from Args:

```
ts: -Level Levels/Menus.level -ServerType ts -EnvironmentSwitcherUrl http://127.0.0.1:51780/ -DataCenter mcvs -SuperCredential game:mcvsoffline -SuperPassword mcvsoffline
gs: -ServerType gs -EnvironmentSwitcherUrl http://127.0.0.1:51780/ -DataCenter mcvs -SuperCredential game:mcvsoffline -SuperPassword mcvsoffline
```

* `GetConfigArgs` splits on every `-` and turns the first space of each piece into the key/value separator: values may
  contain neither (the settings validate the credential/password/datacenter; a base URL with a `-` host name falls back
  to `http://127.0.0.1:<port>/`). Tests replay the exact client parsing.
* `-DataCenter` (verify-C2 §3 row 8) can be dropped with `matchmaker.passDataCenter=false`; the eve URL is
  `context.BaseUrlFor(request)` (the host's own LocalAnubis asks, so loopback).
* `game`: `server_type` = the requested type (`ts`/`gs`; required: the room type defaults to "default"),
  `room_capacity` 5 (ts) / 8 (gs, = MaxPlayers, matches the `capacity` filter), `room_launch_timeout` **0** (a timeout
  makes the Lobby reaper parse a null date), `midgame_join` = LAN mode (`matchmaker.midgameJoin` overrides; solo play
  then never joins a stale launched room), other keys = the LocalAnubis defaults.
* `automatchers`: `pvpdeathmatch` (the quick-match template, `SceneComponents.tx /Matchmaking`) and `dev` (engine
  default), configurable with `matchmaker.automatchers`: `GetAutomacher` uses `First`, an unknown name throws in the
  Finder.
* `matchmaker.tsArgs` / `gsArgs` append validated extra arguments, e.g. `-AllowP2P` (UDP 50034 responder) or
  `-LocalAnubisHost 0.0.0.0 -GSRemoteIp <ip>` for LAN hosts without the cached-PublicHost patch (verify-C6 §2.6).

## Coupons

| Code | Payload (claimed in this order) |
|---|---|
| `UNLOCKALL` | the "mercs" group (`UnlockMerc_{2-21}`, then `ModTier_{1-21}_4`: every merc + every mod), weapons, gear slots, kill signatures, camos, accessories, skins (11,368 rewards; the groups of unlocks.json "order") |
| `UNLOCKMERCS` | the "mercs" group |
| `UNLOCKWEAPONS`, `UNLOCKGEARSLOTS`, `UNLOCKCAMOS`, `UNLOCKACCESSORIES`, `UNLOCKSKINS` | the "mercs" group, then that group |
| `UNLOCKSIGNATURES` | every released avatar, background and sound |
| `UNLOCKPAID` (`UNLOCKPAIDCAMOS`, `UNLOCKPAIDACCESSORIES`) | "mercUnlocks" (`UnlockMerc_{2-21}`, no mods), then the paid-only camos and/or jerseys (docs/ECONOMY.md) |
| `UNLOCKUNRELEASED` | the "unreleased" group (`KillSignature_Avatar_{1513-1518}`); never in UNLOCKALL, `unlockAll` or the shop |

* The game marks a code claimed for the profile before it parses the body, so every built-in code also accepts a
  numeric suffix (`UNLOCKALL2`). Codes are case-insensitive.
* Rewards go to one inbox message (7-day expiry) and are claimed sequentially; merc-dependent rewards of a locked merc
  would turn into alternate rewards, hence the merc unlocks first (verify-C5 (b)). They are explicit per-merc rewards:
  `UnlockMerc_ALL_MAX` claimed fine but crashed every reward display, which looks the merc up by id (device test 3).
* `assets.coupons` in settings.json adds or replaces codes (`{"groups":[...],"rewards":[{"type","quantity"}]}`);
  `assets.couponsEnabled=false` answers 404 to everything. Unknown reward types and `Reset*` are never sent; quantities
  are JSON integers.
* The Redeem button needs `GameFeatures.RedeemCoupons`, forced on by the CRM `_OverrideTables` cell above.
* **The page code** (`admin.redeemCode`, default `MCVSOFFLINE`; empty turns it off; checked before
  `assets.couponsEnabled`): always 404 "coupon does not exist" (a 200 would make the game mark it claimed and add an
  empty inbox message), and it asks the admin portal to open the settings page (trigger `redeem`) only when the request
  is the game's own: `lanMode` off (a LAN guest's redemption runs on this phone's menu server and looks exactly like
  the player's); from loopback; no `Origin`, `Referer` or `Sec-Fetch-*` header, a client id in the path of the game's
  form (`<digits>:<digits>:<version>:<platform>:<store>`) and exactly the game's `User-Agent`
  `[<agent> ](<client id>; <datacenter>)` for it (any web page on the phone could otherwise send the GET, and picking
  the client id `Linux` would match every Android browser's User-Agent); an access token, if any, of the current
  player (the game's coupon request carries none); a host whose in-game browser is not open; at most once per 30 s.
  Logged as `coupon '<code>': opens the MCVS Offline page (requested|ignored: <reason>)`.

## Iris assets

Gameloft's asset service is gone and nothing gameplay-critical needs it: every use falls back to local data or a
default image (report 04 §11). We answer 404 (logged at INFO with the name, so testers see what the game asks for)
unless the file exists in `<data>/assets/` (e.g. an archive made with `tools/cdn-mirror/iris.py`): then the bytes
(Range supported), `/metadata` `{name, hash, size}`, `/metadata/hash` `{"hash": sha256}`, `/url` (our own URL), and the
listing in `GET assets/{cid}`. Names are sanitised (no `..`, no absolute paths).

## Decisions

- **Reuse the Core data-pack reader** (`server/Core/Content`, validated byte-for-byte against `gcbf.py` on all 6,461
  GCBF files) instead of a second GCBF/LZ4 implementation in Crm; `CrmPreconfiguration` adds the CRM-specific lookup,
  retry and compaction. `CrmServiceTests.RealPreconfigurationFromTheDataPack` checks the real 1.17.32 pack.
- **Always 200 for CRM**, complete and validated; never the 404 fallback (it would leave the GS hanging and keep the
  shipped IsDevelopment default, AgeGate and obsolete catalogue).
- **Same document for every user**, except the per-player store filter and the Cosmetic crate's per-player cells (step
  9): no other per-user CRM differences exist offline; the iap language is a setting.
- **No real-money IAP**: `iap: {"prices":[]}`; offering the shipped catalogue could start real Google Play purchases
  that nothing can fulfil.
- **Rooms stay in LocalAnubis**: in Embedded mode quick_launch/automatch/find go to the local Finder; the Federation
  room endpoints are only diagnostics.
- **Coupon payloads are generated at request time** from compact id ranges (`unlocks.json`), which keeps the DLL small
  and lets settings add codes.

## Open questions / device-test checklist

- [ ] TS menu boot with our CRM 200: no age gate, no Crashlytics, IsDevelopment false (6-tap gesture does nothing
      unless `devMenu`).
- [ ] GS start + bot-filled quick match starts immediately (MinPlayers 1 / MaxOpenTimeInSeconds 0) and grants rewards.
- [ ] Native IAP with `{"prices":[]}` calls back `StoreRefreshed` (else the client shop waits; try `crm.iap=preconfig`).
- [ ] Merc detail shows VersusCoins and HC buttons; buying works with VersusCoins.
- [ ] Skins shop and signature screen list our entries; VersusCoins hub tab renders weapons, gear slots, mod tiers,
      crates, packs, VIP; packs grant 10 distinct items.
- [ ] Redeem code button visible; `UNLOCKALL` inbox message (≈11k rewards) claims fully; else use the split codes.
- [ ] Economy multipliers (MUL cells) change match rewards on the GS (`*` on integer columns).
- [ ] The preconfiguration is found in the embedded layout on first boot after the installer finished.
- [ ] With live ops: the TS refetches `configs/users/me` once at each `expiry` (midnight UTC, Monday), not in a loop;
      the weekly modifier cells change the match rewards (and the division perks) after the Monday refresh.
- [ ] Build 9: the Cosmetic crate (STATUS.md, build 9, checks 14–28).
- [ ] Build 9: the "MCVS Offline" inbox message appears (and whether a new weekly timestamp marks it new again); its
      button opens the page; `MCVSOFFLINE` opens it with "code does not exist" behind (design §12 C1, C2, C7).
