# 03 – Player profile, progression data model and persistence

Scope: where MCVS keeps player progression, how it is loaded and saved, what it contains, who is
authoritative, what the game already does offline, and what an offline/P2P build has to provide.
Covers `Gameloft.OnlinePlatform` storage classes (StorageApi/StorageService/Profile/ProfileSection/
StandardProfile/BinaryProfileSection, TransferCode, GameStatsApi, SocialProfile*) and the Game side
(`PlayerProfileComponent`, `ProfileManagerComponent`, `GameProfileSection`, sibling components,
local save files, cheats).

Path abbreviations (all relative to `/home/user/mcvs-work`):

* `OP/` = `decompiled/Gameloft.OnlinePlatform/Gameloft.OnlinePlatform/`
* `EN/` = `decompiled/Gameloft.Engine/Gameloft.Engine/`
* `GM/` = `decompiled/Game/Game/`
* `TP/` = `decompiled/Gameloft.Templates/Gameloft.Templates/`
* `UT/` = `decompiled/Gameloft.Util/Gameloft.Util/`

Legend: **[V]** verified by reading code, **[L]** likely (strong inference), **[S]** speculative.

Companion report: `reports/02-federation-protocol.md` (transport, auth, tokens, discovery). This report
assumes that layer (a local "Federation stub" answering `auth`, `storage`, `config`… on 127.0.0.1).

---

## 0. TL;DR for the implementer

1. **There is no local save of progression anywhere in the game.** The whole player profile lives in
   Gameloft's Seshat storage service (`storage`), in the profile document of the player's account, under
   the key `_mcvs.gameProfile`. The only local files are settings/caches (`MyLocalStorage` JSON,
   `datacenter`, imported friends, crash logs) in `getFilesDir()`. **[V]** (sections 2, 9)
2. **The game server is authoritative, not the client.** The profile is a GameObject that exists on the
   *server* World (title server "ts", and per match the gameplay server "gs"). Every grant/spend/unlock
   runs server side; the client receives a synchronized copy and calls `[Remote]` RPCs. The **server
   uploads the whole serialized profile** (`POST storage profiles/me/myprofile/_mcvs.gameProfile`,
   `object=<full JSON>`) using a `SessionForUser` token; the client's own token never has a storage
   write scope. Seshat itself appears to be a dumb document store (no game validation in client code
   paths). **[V]** (sections 1, 7)
3. **The built-in "offline" mode does not persist anything.** A client link with
   `ServerLocation.None` makes `ProfileManagerComponent` instantiate `DefaultProfile.tx` locally
   (`GM/ProfileManagerComponent.cs:835-853`); that profile has no `GameProfileSection`, so
   `DoSaveProfile` returns immediately (`GM/PlayerProfileComponent.cs:5252-5258`). Offline levels
   (practice range, bot matches) *pause* the TS link and report results back to the TS, which saves to
   storage. With `ServerLocation.Embedded` the TS runs in-process, **but it still loads/saves through
   Federation storage over HTTP**. **[V]**
4. ⇒ **What needs reimplementing for persistence: a Seshat-compatible storage endpoint in the local
   Federation stub** (≈10 request shapes, section 11), backed by one JSON document per account on disk.
   No game patch is required for persistence itself. This is the recommended design and it is also
   the natural place to solve P2P profile hand-off (section 14).
5. Hard requirements found in the load path (any of these broken ⇒ handshake fails ⇒ no main menu):
   * `GET profiles/me/myprofile?include_fields=…` must return **200 + a JSON object** even for a new
     account (a 404 there makes `StandardProfile.SetProfile("")` throw `JsonReaderException`).
   * `GET profiles/me/myprofile/_mcvs.gameProfile` → 404 for "new profile" (or 200 with the stored JSON).
   * New-profile path needs `GET config/configs/users/me` to return an object **containing a `"game"`
     key** (else NullReferenceException in `GameProfileSection.RetrieveCrmServerConfig`), and
     `POST auth/games/mygame/alias` to succeed (player name `agent-<alias>`; **no name ⇒ no save ever**).
   * Saves must answer **exactly `200`** (anything else ⇒ exception, silently swallowed ⇒ data loss).
   * `operation=merge` must be a **deep merge** (else the routine "sections" save wipes `_mcvs.gameProfile`).
6. The profile payload is a **Gameloft.Templates "j" JSON** dump of the profile GameObject (all public
   get/set properties + `[TemplateIgnore(false)]` members of every component on it): currencies, XP,
   mercs (characters) with skins/weapons/camos/accessories/mods/gear slots, league/division, gear
   inventory, achievements, tutorial flags, inbox, stats, subscription, daily shop… (section 5). It is
   human-readable and editable. Versioning/migration is done on load by
   `ProfileManagerComponent.ValidateProfileVersion` (current profile version constant 57).
7. **Unlock-all**: the game already contains server-side helpers used by the developer cheat menu
   (`CheatsComponent`: UnlockAllMercs, UnlockAllMercSkins, UnlockAllMercWeapons, Unlock all weapon
   camos, Unlock all accessories, Unlock All Mods, Unlock All Gear Slots, AllSignatures, MaxCurrencies…).
   Cleanest hook: a tiny IL patch in `PlayerProfileComponent.OnProfileLoaded()` (server branch) that calls
   those helpers when a toggle is set; the result is then persisted by the normal save. Zero-patch
   alternatives: (a) make the player a "valid developer" (local IPs are always in the dev edge list;
   `IsDevelopment` comes from CRM) so the in-game cheat menu works, or (b) let the stub grant items via
   the IAP `inventory` ledger which the TS diffs and grants on every login (section 13).

---

## 1. Architecture: who owns the profile

### 1.1 Server-side profile object **[V]**

* `World.Profile` is `ProfileManagerComponent` (`GM/ProfileManagerComponent.cs:19`, base
  `EN/ProfileManagerComponentBase.cs`). Profiles are GameObjects under MetaGame → `Profiles`, named by
  fed id (`GM/ProfileManagerComponent.cs:99-120, 856-870`).
* The profile GameObject is instantiated from the template `DefaultProfile.tx` (OBB data,
  `GM/ProfileManagerComponent.cs:947-950`) and carries `PlayerProfileComponent` plus sibling components
  (`LeagueComponent`, `DivisionsComponent`, `GearsComponent`, `SubscriptionCardComponent`,
  `DailyShopComponent`, `DailyRewardComponent` – see 5.3).
* `PlayerProfileComponent : MainProfileComponent : ProfileComponent : UserComponentBase`
  (`GM/PlayerProfileComponent.cs:24`, `EN/MainProfileComponent.cs:11`, `EN/ProfileComponent.cs`).
* Load and save happen **only in the server World**: `ProfileManagerComponent.LoadPlayerProfile` is
  called from the server handshake delegates (`GM/GameServerComponent.cs:943`, `GM/AgeGateChecker.cs:71`);
  `AutoSave` only ticks `if (World.IsServer())` (`GM/PlayerProfileComponent.cs:4736-4742`).
* Two server roles both load the profile: **TS** (title/meta server, `ServerType=="ts"`) and **GS**
  (gameplay server, `"gs"`) (`GM/ServerComponentExtension.cs:7-15`). The GS also saves every
  connected player's profile at match end (`GM/GameplayComponent.cs:898-910`) and disables further
  saves; when the player comes back, the TS **reloads** from storage (`GM/GameServerComponent.cs:735-757`).
* The client World gets the profile by synchronization (`[SyncReliable]` properties) and talks to the
  server via `[Remote(Client, Server)]` RPCs: e.g. `SetPlayerName` (`GM/PlayerProfileComponent.cs:3636`),
  `ForceSaveProfile` (`:5304`), cheat requests (`GM/CheatsComponent.cs:950`), `RegisterRoomInProfile`
  (`GM/GameClientComponent.cs:1438`). Currency/unlock mutators (`AddCurrency`, `SpendCurrency`,
  `UnlockMerc`, …) mutate the server copy and push notifications to the client
  (`GM/PlayerProfileComponent.cs:3098-3290`).

### 1.2 Sessions and scopes used for storage **[V]**

| Who | Session | Scopes | Evidence |
|---|---|---|---|
| Server, per player | `SessionForUser` opened with the game super credential `for_username=<player>` | `InitialScopesServer` (incl. `storage`, `storage_ro`, `storage_restricted`) | `EN/Connection.cs:1564`, `EN/AuthenticationComponent.cs:272-277, 576-608` |
| Server, own | game-server session (`SuperCredential`) | same | `EN/AuthenticationComponent.cs:439-470` |
| Server, read-only view of others | `SessionForUser.GetReadOnlySessionForUser` (reuses server tokens) | – | `GM/ProfileManagerComponent.cs:1011` |
| Client | anonymous session | `auth lobby config` only (no storage write) | `EN/AuthenticationComponent.cs:270` |

`ProfileSection.Scope` default is `storage_ro storage_restricted`, `Visibility` default `Restricted`
(`OP/ProfileSection.cs:54-56`) – i.e. the game profile is written with "restricted" visibility, which only
a token carrying `storage_restricted` (the game server) may write. **[V]** for the code; **[L]** for the
server-side ACL meaning.

### 1.3 "Is the server or the client authoritative?"

* **Profile state:** server (TS/GS). The client never writes progression to storage. **[V]**
* **Storage backend:** stores whatever blob the game server sends (`object=` full JSON). No client code
  expects storage-side validation or recomputation; responses are only read back as documents. **[L]**
* **Offline bot matches:** hybrid. The client plays the match locally and sends a control message
  `OfflineMatchAgainstBotsOver` (`matchResult`, `mercsUsed`, `agentScores`, `isMVP`, `isMatchFinished`) to
  the TS, which applies pre-rolled rewards (`OfflineMatchWinRewards`/`OfflineMatchLoseOrDrawRewards`),
  trophies, merc XP and saves (`GM/OfflineBotsGameplayComponent.cs:528-630`). Result honesty is
  trusted from the client. **[V]**
* **IAP:** the purchase ledger (`inventory` section) is written by Gameloft's e-commerce backend; the TS
  diffs it against `CrmStandardData.Inventory` stored in the profile and grants the difference
  (`EN/IapStoreComponent.cs:1243-1305`, `GM/PlayerProfileComponent.cs:6055-6150`). **[V]**

---

## 2. Storage layer (Gameloft.OnlinePlatform)

### 2.1 `StorageApi` request builders **[V]** (`OP/StorageApi.cs`)

Service key `storage` (located via pandora, see report 02). Paths are relative to the storage base URL,
except those starting with `/` (host-absolute). Credential in path = `credential.ToUrlEncodedString()` =
`Uri.EscapeDataString("type:user")` (e.g. `fed_id%3A9f2c…`), or the literal `me` for `Credential.Me`
(`OP/Credential.cs:107-124`). Token goes in query (GET) or form body (POST) per report 02.

| Builder | Method & path | Params | Lines |
|---|---|---|---|
| `GetProfileRequest(cred, scope, selector, includeFields)` | `GET profiles/{cred}/myprofile[/{selector}]` | `include_fields=a,b.c,…` (only if list non-empty) | 98-111 |
| `SetProfileRequest(cred, scope, object, selector, visibility, operation, etag)` | `POST profiles/{cred}/myprofile[/{selector}]` | `object=<json>`; `operation=add|increment|append|merge|batch_set|delete|batch_delete` (omitted for `set`); `visibility=` `{"*":"<vis>"}` when no selector, else `<vis>`; header `If-Match: <etag>` | 113-136 |
| `GetProfileFieldRequest` | `GET profiles/{cred}/myprofile/{selector}` | – | 138-142 |
| `GetBatchProfiles(credentials, include_fields)` | `GET /profiles` | `credentials=c1,c2`, `include_fields` (scope `storage`) | 144-154 |
| `PostDeleteProfileRequest` | `POST profiles/{cred}/myprofile/delete` | – | 156-163 |
| `GetDataRequest`/`PutDataRequest`/`SearchDataRequest`/`DeleteDataRequest` | `GET|POST data/{cred}/{key}`, `GET data`, `POST data/me/{key}/delete` | `data, visibility, expiration, category, indexes, meta_data`, `filters, limit, offset, randomize` | 165-232 |
| `GetMatchesRequest` | `GET /profiles/matchers/{name}/matches` | `limit, include_fields, …` | 234-251 |
| `SetVisibilityObject`/`GetVisibilityObject` | `POST|GET /profiles/me/myprofile/visibility[/{sel}]` | `visibility` | 253-269 |
| `GetNearMeRequest` | `GET profiles/location/{cred}` (scope `storage_admin`) | `distance, limit` | 271-279 |
| `GetProfileBackupsRequest` / `…ById` | `GET /profiles/{cred}/{name}/backups[/{id}]` (scope `storage_admin storage_backup`) | `start_date, end_date` (ISO-8601) | 281-296 |

Enums: `Operation {Set, Add, Increment, Append, Merge, BatchSet, Delete, BatchDelete}`,
`Visibility {Private, Public, PublicWrite, Restricted, PrivateRestricted, Hidden}` → strings
`private, public, public_write, restricted, private_restricted, hidden` (`OP/StorageApi.cs:12-96`).

**Used by MCVS:** profile get/set/delete, batch profiles, backups. **Not used anywhere in Game/Engine:**
`data/*` (PutData/GetData/SearchData/DeleteData), matchers search, visibility objects, near-me
(grep over `decompiled/Game` and `decompiled/Gameloft.Engine`). `BinaryProfileSection` has **no
subclass** in the game (grep `: BinaryProfileSection` → none). **[V]**

### 2.2 `StorageService` **[V]** (`OP/StorageService.cs`)

* `GetProfile()` / `GetProfile(cred)` – per-credential cached `Profile` objects (lines 42-56).
* `GetProfileField(selector[, scope="storage_ro", cred=Me])` → raw body string (58-67).
* `SetProfileField(selector, value[, scope="storage", cred=Me, visibility, operation=Set])` →
  `SetProfileFieldWithResponse`, which after the POST also patches the cached `standard` section's
  JSON (`SelectToken(selector).Replace(JToken.Parse(value))`) (95-123). `value` must be valid JSON
  (e.g. `"\"en\""` for a string).
* `DeleteProfileField(selector)` = `SetProfileField(selector, null)` (125-138) → `object=` empty.
* `DeleteProfile(scope="storage")` → `POST …/myprofile/delete` (140-154).
* `GetProfileBackups` parses a JSON **array** of `{level,size,totalSpend,date,id,index}` (14-40, 230-235);
  `GetProfileBackupById` returns a JSON object (237-241).

### 2.3 `Profile` and `ProfileSection` **[V]**

`Profile` (`OP/Profile.cs`) holds named sections (`Dictionary<string, ProfileSection>`; the name is
only a cache key, the storage path is the section's `Selector`). Constants: `GameSectionName="game"`,
`CheatsSectionName="_cheatProfile"` (lines 11-13). `GetProfileSection<T>(name, ct)` lazily creates via
`SuperActivator.CreateInstance(typeof(T), profile)` and loads if not loaded (44-67).

`ProfileSection` (`OP/ProfileSection.cs`):

| Member | Behaviour |
|---|---|
| `Selector` | dotted path inside the account's profile document; `""` = document root |
| `IncludeFields` | list of `Field(name, readOnly)`; used for `include_fields` and for merge saves |
| `JsonProfile` / `ProfileString` | in-memory JSON; `SavedProfileString` = last loaded/saved |
| `IsModified` | `NormalizeJsonString(saved) != NormalizeJsonString(current)` (line 48) – saves are skipped when unchanged |
| `Load` → `LoadJson` | `GET profiles/{cred}/myprofile/{selector}?include_fields=…`; body = section JSON; ETag kept. On `OnlineRequestFailedException` with **404 or 403** → `CreateProfile()` (119-140, 161-172) |
| `CreateProfile` | `ProfileString ??= "{}"`, then `Save()` (232-239) |
| `Save(op=Set, useEtag=true)` | skip if unmodified; `op==Merge` ⇒ only non-readonly `IncludeFields` are sent; `POST …/{selector}` with `object`, `visibility`, `operation`, `If-Match`. **Response status must be exactly 200** else `OnlineRequestFailedException`; stores response `ETag` (194-230) |
| `Delete` | `POST …/{selector}` with `object=""` and `operation=delete`, then local `{}` (152-159) |
| `LoadSections(list)` (static) | **one** `GET profiles/{cred}/myprofile?include_fields=<union>` with scope union; each section picks `SelectToken(selector)` (no IncludeFields) or builds an object from its fields. Missing selector-section ⇒ `CreateProfile`. If the GET fails with 404/403, `jsonProfile` stays null ⇒ selector sections are created, but a field-list section gets `SetProfile("")` ⇒ `JsonReaderException` (269-335) |
| `SaveSections(list)` (static) | builds one nested object of all *modified* sections (selector path created as nested objects, field sections contribute their non-RO fields at the root) and sends **one** `POST profiles/{cred}/myprofile` with `operation=merge`, no visibility, no If-Match; must return 200 (342-391) |

`BinaryProfileSection` (unused by MCVS): `data/{cred}/{selector}`, payload
`base64(deflate(utf8(json)))` (Fastest, Optimal if >1 MiB), `category=profile`,
`meta_data={"description":…}`, scope adds `storage_admin` (`OP/BinaryProfileSection.cs:18-133`).

### 2.4 `StandardProfile` (account-level fields, selector `""`) **[V]** (`OP/StandardProfile.cs`)

Include fields (RO = server-maintained, never sent on save) (lines 343-375):

| Field | Type / meaning | RO |
|---|---|---|
| `credential` | `type:user` of the profile owner | RO |
| `created`, `modified` | ISO-8601 | RO |
| `datacenter` | string | RO |
| `last_session` | ISO-8601 | RO |
| `total_spent`, `total_transactions` | int | RO |
| `last_purchase` | ISO-8601 | RO |
| `devices` | array of `{carrier, download_code, firmware, id, model}` | |
| `level`, `xp` | int – copied from `PlayerProfileComponent.Level/Xp` on each save (`GM/PlayerProfileComponent.cs:5297-5302`) | |
| `total_playtime` | seconds (double) | |
| `country`, `language` | strings | |
| `birthdate` | ISO-8601 or null (age gate, `GM/AgeGateChecker.cs:79-85`) | |
| `gender` | `male`/`female`/`declined` | |
| `location` | `{lat, lon}` | |
| `clan`, `pending_clan` | clan id / `{id, request_id, created}` | |
| `carrier`, `available_transports` (string[]), `current_client_id`, `current_device` (Device), `current_device_id` | | |
| `banned_from` | object | RO |

`Save()` always uses `Operation.Merge` and **no ETag** (`Etag => null`) (96, 377-380). Root save body is
`object={…16 writable fields…}&visibility={"*":"restricted"}&operation=merge`.

### 2.5 Other storage-adjacent APIs **[V]**

* **TransferCode** (`OP/TransferCode.cs`): `{ "value": "<code>", "expiration": "<ISO>" }` from
  `POST auth users/me/transfer_code`; redeem via `GET auth authorize?grant_type=transfer_code…`
  (report 02 §3.6). Account-transfer between devices; irrelevant offline (could be emulated stub-side).
* **GameStatsApi** (`OP/GameStatsApi.cs`): `GET gamestats/users/queries/default|game_stats_by_time`
  (`category, group_keys, start, end`), `POST gamestats/users/me/events/default` (`category, keys,
  aggregates`), scope `game_stats`. Only used by `GameMatchmakingComponent` for matchmaking telemetry
  (`GM/GameMatchmakingComponent.cs:1276,1337,1365,1436`) – **not progression**. Stub: `200 []`/`200 {}`.
* **SocialProfile** (`OP/SocialProfile*.cs`): `GET social accounts/{cred}` →
  `{"credential","name","groups":[…]}`; `POST social accounts/me` (`name, language, country`);
  `POST social accounts/me/status_line`. Used to publish the player name
  (`GM/PlayerProfileComponent.cs:2313-2322, 3636-3700`); errors are swallowed.

---

## 3. The MCVS profile document

`GameApplication.GameProfileSelector` comes from `GameDefinitionComponent.GameProfileSelector`
(code default `"game"`, `EN/GameDefinitionComponent.cs:204`, overwritten by template data). The
production value is **`_mcvs`**: hard-coded paths `jObject["_mcvs"]["gameProfile"]…`
(`GM/ProfileManagerComponent.cs:137, 160`) and `"_mcvs.snapshot.MatchMakingRating"`
(`GM/GameMatchmakingComponent.cs:1029`). **[L, very strong]**

Reconstructed account document (Seshat "myprofile") as the game reads/writes it:

```jsonc
{
  // StandardProfile (section "standard", selector "")
  "credential": "anonymous:<anon-user>", "created": "2026-10-24T12:00:00Z", "modified": "…",
  "datacenter": "local", "last_session": "…", "total_spent": 0, "total_transactions": 0,
  "last_purchase": null, "devices": [], "level": 7, "xp": 5120, "total_playtime": 3600.0,
  "country": "US", "language": "en", "birthdate": null, "gender": "declined", "location": null,
  "clan": null, "pending_clan": null, "carrier": "", "available_transports": [],
  "current_client_id": "2620:66102:1.17.32:android:googleplay", "current_device": {}, "current_device_id": "",
  "banned_from": null,

  "inventory":  { },          // IapStoreComponent.InventorySection – IAP purchase ledger {rewardKey: count}
  "_inventory": { },          // AdsComponent.AdsInventorySection
  "language": "en",           // (also written alone by LocalizationComponent)

  "_mcvs": {
    "gameProfile":  { "@version": "0.1", "$default": { /* Templates dump, section 4 */ } },
    "snapshot":     { "PlayerName": "agent-123456", "IsKayac": false, "PlayerLevel": 7, "HardCurrency": 50,
                      "Credits": 300, "LastLogin": "…", "Trophies": 120, "MatchMakingRating": 1000,
                      "Agents": 3, "SeasonTickets": 0, "VersusCoins": 0, "UpgradeTokens": 10,
                      "KillSignature": { … } },              // GM/PlayerProfileComponent.cs:4991-5006
    "matchers":     { "LoginsPerDay": 1, "Agent1_Owned": 1, "PlayerLevel": 7, "Credits": 300, … },
                                                              // CRM segmentation, GM/PlayerProfileComponent.cs:5007-5251
    "currentMatch": { "CurrentServerType": "gs", "CurrentServerId": "<roomId>",
                      "CurrentRoomUser": "<RoomUser json>", "ReconnectAttemptsCount": 0,
                      "CurrentMatchComposition": { … } }    // GM/CurrentMatchAccessor.cs:20-82
  }
}
```

Sections used by the game:

| Section class | Cache name | Selector (path) | Scope | Visibility | Who | Evidence |
|---|---|---|---|---|---|---|
| `GameProfileSection` | `game` | `_mcvs.gameProfile` | storage_ro storage_restricted | restricted | TS/GS | `GM/GameProfileSection.cs:100-103` |
| `MatchersSection` | `matcher` | `_mcvs.matchers` | " | restricted | TS | `GM/MatchersSection.cs` |
| `SnapshotSection` | `snapshot` | `_mcvs.snapshot` | " | restricted | TS; others read it | `EN/SnapshotSection.cs`, `EN/SocialComponent.cs:798` |
| `StandardProfile` | `standard` | `` (root, field list) | " | `{"*":"restricted"}` | TS + many | `OP/StandardProfile.cs` |
| `IapStoreComponent.InventorySection` | `inventory` | `inventory` | " | restricted | TS | `EN/IapStoreComponent.cs:160-167` |
| `AdsComponent.AdsInventorySection` | `adsInventory` | `_inventory` | " | restricted | TS | `EN/AdsComponent.cs:20-27` |
| `CurrentMatchAccessor` (not a section) | – | `_mcvs.currentMatch` | storage_admin | – | TS/GS (game-server session) | `GM/CurrentMatchAccessor.cs:110-148` |
| `CheatSection` | `_cheatProfile` | `_cheatProfile` | storage_admin | hidden | **unused** | `EN/CheatSection.cs` |
| (CRM bootstrap) | – | `_mcvs` of the **game server's** own account | storage_ro storage_restricted | – | server, only if CRM says "needs a profile" | `EN/CRMServerSettingsComponent.cs:131-171` |

---

## 4. `GameProfileSection`: format, creation, versioning

### 4.1 Format – Gameloft.Templates JSON ("j") **[V]**

* Save: `GameProfileSection.FromGameObject(go)` = `TObject.FromObject(go).ToString("j")`
  (`GM/GameProfileSection.cs:105-109`); `TObject.ToString("j")` uses `JsonTemplateWriter`, compact
  (`TP/TObject.cs:90-104`).
* Load: `GetTemplate()` parses the string with `TemplateReader.FromStream(…, "DefaultProfile.tx")
  .ReadDefaultTemplate()`; empty/`"null"`/`"{}"` ⇒ `null` (= use default) (`GM/GameProfileSection.cs:117-127`).
  The section overrides `JsonProfile` to `null` – it is **opaque text** to the platform layer (43-53).
* Writer layout (`TP/JsonTemplateWriter.cs:91-208`): root `{"@version":"0.1", "$default": <object>}`;
  node names are qualified: property `Name`, child `/Name`, attribute `@name`, template `$name`, index
  `[i]`; a type is the attribute `@type` (`TP/TNode.cs:12, 152-161`, `TP/TName.cs:121-147`). Objects with a
  **single** property are collapsed into a dotted key (`"A.B": v`) (`TP/JsonTemplateWriter.cs:145-158`).
  ⇒ the stub must treat `_mcvs.gameProfile` as an **opaque JSON value** and never split its inner keys on
  `.`/`/`.
* What gets serialized (`TP/DefaultConverter.cs:351-369, 396-432`, `UT/PropertyReference.cs:579-587`):
  every instance property **and field** (public and non-public) that is readable+writable and either
  public-get/public-set or marked `[TemplateIgnore(false)]`; anything `[TemplateIgnore(true)]` is skipped.
  A `GameObject` contributes its children (components and child objects) as `/Name` nodes. The dump is
  **complete** (no diff against the default template; `CreateTemplateNode` = `new TObject()`,
  `TP/DefaultConverter.cs:267-270`).
* Known path inside: `$default → /Profile → Version` (`GM/ProfileManagerComponent.cs:137`), i.e. the
  `PlayerProfileComponent` is the child named `Profile`. Other component child names (e.g. `/League`)
  are **[S]** (defined by `DefaultProfile.tx`, which is in the OBB we do not have).

### 4.2 New-profile creation (`GameProfileSection.CreateProfile`) **[V]** (`GM/GameProfileSection.cs:143-182`)

Triggered by 404/403 on `GET …/_mcvs.gameProfile`. Requires a `SessionForUser` (server-only; else
`InvalidOperationException`).

1. `Session.GetAccountInfo()` → `GET auth users/me` (cached). If the account has **exactly one
   credential and it is `anonymous`** →
2. `RetrieveCrmServerConfig`: opens a **new** super session (`OpenSession(Super, SuperPassword,
   "mcvsGameServer")`) and reads CRM `configs/users/me` → `(JObject)((JObject)cfg["game"])["_ServerConfig"]`.
   An `OnlineRequestFailedException` is caught (⇒ null), **but a response without `"game"` throws a
   NullReferenceException that is not caught** (60-75).
3. If `Session.ClientId` is not in `UnsupportedLinkFrom` (CRM list or default
   `72571, 72147, 61134, 66102, 66354` – **our client id `2620:66102:…` is in the default list**, so this
   block is skipped unless CRM overrides it): for every other account on the device
   (`GET auth users?device_id=…` → array) that has a profile, relink the anonymous credential to it and
   throw `MustRetryHandshakeException` (account auto-merge) (153-179).
4. `SetProfileField("_mcvs", "{\"gameProfile\":null}", "storage_ro storage_restricted", Me, Restricted)`
   → `POST profiles/me/myprofile/_mcvs` `object={"gameProfile":null}&visibility=restricted` (181).
   (This is a **set** on `_mcvs`, i.e. it replaces the whole `_mcvs` object – harmless on a new account.)
5. `ProfileString` stays null ⇒ `GetTemplate()` = null ⇒ default profile; the first save happens right
   after load (4.3).

`GameProfileSection.SaveImplementation`: on `412 Precondition Failed` it calls `Load()` (re-read storage)
and returns without retrying (184-199). See section 8.

### 4.3 Version validation & migration (`ProfileManagerComponent.ValidateProfileVersion`) **[V]**
(`GM/ProfileManagerComponent.cs:180-337`)

Profile version constants: `LaunchVersion=52, SixPointOne=53, SeventeenPointFour=55, TenPointTwelve=56,
TenPointEighteenHotfix=57` (76-84); target `ProfileVersion` is a template property (97, value **[S]**
57). Steps on every load:

1. `RollbackBrokenUpd10Profiles`: if `Profile.Version == 56`, fetch storage backups from the last 60 days
   (`GET /profiles/{fedId}/myprofile/backups?start_date&end_date`, then `/backups/{id}`) and take the
   newest with `Version < 56`, else the default profile (130-178).
2. Version < 52 ⇒ **full reset** to default (keeps only `FinishedTutorial`). 52/53 ⇒ tutorial-progress
   checks may reset. 54 ⇒ XP rescale table. <57 ⇒ fix `League.ClaimedRankUpRewards`. <56 ⇒ merc XP →
   mastery conversion and career autoclaim (`ConvertMercXpForUpdate10`, `AutoclaimCareerRewardsForUpdate10`).
3. Always removes a legacy `/DailyCrate` child.
4. `GameVersion` (saved as `MainProfileComponent.GameVersion`): if the **saved game version is newer than
   the running one** ⇒ `ClientGameVersionIsGreaterThanServerGameVersionException` (handshake fatal, 266-270).
   If feature versions differ ⇒ `ResetIsNewUserInUpdate` (resets to default if the user never played
   offline/PvP); several 1.3/1.5/1.8/1.10 migrations (`ResetTrophiesForUpdateThree/Five`,
   `WeAreBrawlStarsNowBois` currency conversion, `ConvertSkinsAndCamos`, `FixTrackingEnums`,
   `FixTutorialStepsOnLoad`, `FixBrokenStats`).
5. Sets `Version = ProfileVersion`, `GameVersion = running version`, then **merges the loaded template onto
   a fresh `DefaultProfile.tx`** (`tObject.MergeWith(template)`, 330-336) ⇒ fields missing from a save
   take default values; extra unknown fields are reported via the template error handler. **[V]**
6. Returns `isValid=false` when reset ⇒ the caller saves immediately (`GM/ProfileManagerComponent.cs:892-894`).

Implication: a stub never needs to do migrations; saves written by 1.17.32 load cleanly in 1.17.32. Do not
feed a save from a newer APK into an older one.

---

## 5. Data model – what "full progression" consists of

### 5.1 `PlayerProfileComponent` persisted members (`GM/PlayerProfileComponent.cs`) **[V]**

Extracted with the `KeepProperty` rules (public get/set or `[TemplateIgnore(false)]`, not
`[TemplateIgnore(true)]`). "Sync" = also `[SyncReliable]` to the client. Grouped by topic:

**Identity / account**
`PlayerName` (string, Sync, must be non-empty for saves), `LastPlayerNames` (List<string>),
`PlayerNameChangeCount`, `PlayerNameFreebiesUsed`, `Avatar`, `ClientId` (field), `NicePlayCredentials`
(List<string>), `IsKayac`, `Is9sPlayRegion`/`is9sPlayRegion`, `IsQA`, `IsDevelopment`,
`WantsNonDevErrorMessages`, `IsDatacenterValidationRequired` (default true), `ProfileOwnerRoomId` (profile
lock, §8), `InstallationDate`, `LoginsPerDay`, `LastLoginDate`, `ConsecutiveDaysLoggedIn`.
Inherited (`EN/MainProfileComponent.cs`, `EN/ProfileComponent.cs`): `Alias`, `IsDeveloper`, `Region`,
`FirstRegion`, `GameVersion`, `TotalPlayTimeSaved` (ticks), `Version`, `AutoSaveIntervalMinutes` (uint,
default 5 – note: this config value is itself persisted), `EnableSave`.

**Currencies** – `protected Dictionary<string,int> Currencies` (`[TemplateIgnore(false)]`, line 506), keyed
by `CurrencyType` names (`GM/CurrencyType.cs`): `SoftCurrency` (obsolete), `HardCurrency`, `AgentsCurrency`
(obsolete), `MercPortrait`, `UpgradeTokens`, `Credits`, `SeasonTickets`, `VersusCoins`, `Shards`.
Accessors `HardCurrency/Credits/UpgradeTokens/VersusCoins/SeasonTickets` are computed
(`[TemplateIgnore(true)]`, 859-888). Also `UpgradeTokensEarned/Spent`, `CurrenciesTracking`,
`CurrenciesTrackingHistory` (Dictionary<string, CurrencyData{CurrencyType, Stack[{Quantity,
PaiementType}]}>), `ConversionSummary`, `OldUICredits`, `OldUILootCrates`, `CratesWonFromConversion`,
`PVPCurrencyRewardCountForWins`, `VersusCoinsRewardCountForWins`. API: `AddCurrency`, `SpendCurrency`,
`HasCurrency`, `GetCurrency`, `SetCurrency` (3098-3300). No balance cap in these methods.

**Player XP / level** – `Xp` (int, `[TemplateIgnore(false)]`, protected setter, 1092); `Level` is **not**
persisted (private setter) and is recomputed from `Xp` with table `PlayerLevelTable` (`ComputeLevel`,
4266-4295); `LastGeneratedRewardsLevel`, `LastLevelUpRewards` (List<RewardItem>), `PlayerLevelTable`,
`PlayerUnlockedTableName`, `PlayerStatsBnousTableName` (table names, persisted).

**Mercenaries (characters)** – `Mercs : List<Merc.ShortDef>` (1257, Sync), plus `MercsInTrial`
(List<MercTrial{Id, Level, Mods, TrialEndDate, Active, PointcutTriggered}>), `MercsShopSeen`,
`SkinsShopSeen` (Dictionary<string, MercShopSeen{MercId, Prices}>), `MercsBundlesActive`,
`MercsInShopCrates`, `MercsUnlockedInOrAfterUPD5`, `MatchesPlayedUsingMerc` (Dictionary<int,int>),
`AgentTiers` (Dictionary<string,int>). `Merc.ShortDef` (`GM/Merc.cs:10-200`):

```text
Id string | Level int=1 | Xp int | Fatigue int | Locked bool=true | IsNew bool=true
MasteryToClaim List<int> | AutoclaimMasteryLevel int
Skins List<string>=["1"] | EquippedSkin "1" | NewSkins
Weapons List<string> | WeaponsFragments Dictionary<string,int> | EquippedWeapon | NewWeapons
WeaponInTrial List<WeaponTrial{Id,TrialEndDate,Active,PointcutTriggered}>
Accessories | EquippedAccessories | NewAccessories
GearSlots List<GearSlotData{Locked=true}> (6) | Camos ["1"] | EquippedCamo "1" | NewCamos
Mods List<ModData{Id, Locked=true}> | NewMods | UpgradeSeen
```

`MercsComponent.ProfileLoaded` adds a locked `ShortDef` for every merc spec missing from the list
(`GM/MercsComponent.cs:2231-2255`) – so after load the list covers all mercs in the data tables. Ownership
checks are simple list/flag tests: `IsMercUnlocked` (`!Locked`), `IsMercSkinUnlocked` (`Skins.Contains`),
`IsMercWeaponUnlocked` (`Weapons.Contains` or active trial), `IsWeaponCamoUnlocked`,
`IsMercAccessoryUnlocked` (`GM/PlayerProfileComponent.cs:3764-4060`).

**League / trophies / matchmaking** – `GentleTrophies`, `GentleTrophiesBest`,
`GentleTrophiesAutoclaimMin/Max`, `MatchMakingRating`, `Deviation` (Glicko), `MMRFilterId`,
`WinLossStreak`, `MatchResultTrackers`, `MatchResultTrackerCount`; sibling `LeagueComponent`
(`LeagueDivisionId`, `AvailableRankUpRewards`, `ClaimedRankUpRewards`, `OldLeagueDivisionId`,
`OldTrophies`, `Table="PVPLeaguesList"`) and `DivisionsComponent` (`Division`, `DivisionTrophies`,
`DivisionTrophiesBest`, `Data{Id, SeasonId, ActiveLeaderboardId, IsQA, EndDate, CachedRank}`,
`SeasonEndedInfo`, `Table="DivisionsList"`).

**Stats** – `Kills`, `Deaths`, `Headshots`, `MVPs`, `Eliminations`, `Wins`, `AllTimePVPMatches`,
`AllTimeOfflineVictories`, `ZoneCaptured`, `TLECompleted`, `LastGameResult`, `SessionHistoryMap`
(Dictionary<string, SessionHistory{AllTime, Last[{DateTime, Count}]}>), `PerMapSessionCount`,
`ClaimedCrates`, `LatestEndMatchRewards`, `BotMatchesDoneInTheDay`, `BotMatchesAvailable`,
`OfflineMatchWinRewards`, `OfflineMatchLoseOrDrawRewards`, `OfflineMatchConnectionType`.

**Cosmetics (player-level)** – kill signature: `KillSignatureBackgroundId/AvatarId/SoundId`,
`KillSignatureBackgrounds/Avatars/Sounds`, `KillSignatureNew*`.

**Tutorial / FTUE flags** – `FinishedTutorial`, `FTUEFinishedSequences` (List<string>, field 561),
`FTUEActivated`, `PVPActivated`, `FTUERewardsClaimedCount`, `FTUETapReminderQuickMatchCount`,
`FTUETapReminderTrainingCount`, `FTUECratesPoolId`, `IsNewUserInThisUpdate`, `UpdateFiveOldUserTutoSeen`,
`LastTutoVersionSeen`, `ScreensVisisted`, `VisitedScreens`, `StartingInventoryLoaded`,
`IsFirstLaunchTracked`, `HasTrackingData`.

**Achievements / quests / events** – `Achievements` (List<AchievementData{Id, Progression}>, 1000),
`TleAchievements` (List<TimeLimitedAchievementEvent>), `FinishedEvents` (used by matchers),
`ActiveTournamentTicket`, `ClanRewardsSent`.

**Inbox / shop / monetisation** – `InboxMessages` (List<InboxMessage> with rewards), `CrmStandardData`
(`Inventory`, `AdsInventory` – the *last seen* IAP/ads ledgers, `CRMLimits`, `FirstLaunch`; 729,
`EN/CrmStandardData.cs`), `PurchasedPacks`, `SeenPacks`, `SeenBanner`, `IAPBundlesSeen`,
`CustomBundlesSeen`, `CustomBundleLimitData`, `StoreCarouselHistory`, `BlackMarketSlotNumber`,
`BlackMarketLastRegenServerDate`, `BlackMarketServerNextGenerationDate`, `NextAdsShopRefreshTime`,
`CurrentAdShopStage`, `NextTimeCanShowAd`, `FreeCrateDeadline`, `FreeCratesAvailable`,
`CratesTrackVersion(TableName)`, `CratesTrackWinsCount`, `HasReceivedTopUpReward`,
`LotteryWeaponsWonByRarity`, `seedMap` (deterministic RNG seeds), `ModernCombat5LoginFreeReward*`,
`DoNotShowTodayFlags`, `PerClientIdUpdatePopupInfos`, `TrackingData`, `TrackingDataPending`,
`EnableDemoMode`.

### 5.2 Daily / weekly progress **[V]**

* Login counters: `ProfileManagerComponent.UpdateLoginStats` (TS only) on load and every save
  (`GM/ProfileManagerComponent.cs:905-945`): `LoginsPerDay`, `ConsecutiveDaysLoggedIn`, `LastLoginDate`,
  events `FirstLoginOfTheDay`/`DailyLoginsIncreased`/`ConsecutiveDaysLoggedInIncreased`.
* Daily shop (`DailyShopComponent.currentEntries`, `NextRefreshTime`), daily rewards
  (`DailyRewardComponent.subscriptions`), VIP card (`SubscriptionCardComponent.subscriptions`, …).
* Seasons/divisions and daily quests are **driven by CRM time-limited events** (`TimeLimitedEventsComponent`)
  and leaderboards (`DivisionsComponent.OnNewSeasonStartedServer` reads `LeaderboardService…SelfData`,
  `GM/DivisionsComponent.cs:344-460`). Offline, they only advance if the stub serves TLE events and
  leaderboards (CRM / leaderboard analysts).

### 5.3 Sibling components on the profile GameObject **[L]**

Fetched via `profile.GetComponent<…>()` / `World.Profile.GetUserComponent<…>()`:
`LeagueComponent` (31 uses), `DivisionsComponent` (32), `GearsComponent` (25: `GearInventory`
SynchronizationFriendlyDictionary<int, GearData{IsNew, IsEquipped, Tier, Level, Stats, Sub, Slt,
NextSubstat…}>, `GearIdCounter`, `MaxGears=400`), `SubscriptionCardComponent`, `DailyShopComponent`,
`DailyRewardComponent`. Exact list and child names come from `DefaultProfile.tx` (OBB).

Metagame managers (`MercsComponent`, `WeaponsInventoryComponent`, `CratesInventoryComponent`,
`MercSkinsComponent`, `WeaponCamosComponent`, `ModsMetaComponent`, `InboxComponent`, …) hold only static
**per-user data tables** (`PerUserDatabase`, CRM-overridable via `_OverrideTables`,
`EN/CRMComponent.cs:246-291`, `EN/CRMTableChanger.cs`), not player state.

### 5.4 Settings (not in the profile) **[V]**

`LocalStorageComponent` → file `MyLocalStorage` (JSON object) via `SaveStream`
(`EN/LocalStorageComponent.cs:16-110`). Contents: `UserPreferencesComponent`/`GamePreferencesComponent`
properties (`MasterVolume, MusicVolume, SFXVolume, VOVolume, CameraSensitivity, CameraSmoothing,
InvertCameraX/Y, SprintSensitivity, EnableAutoShoot, AutoShootAlwaysActive, IronSightButton,
IronSightMode, AimBehavior, AbilityButtonPlacement, SingleTapExitIronSight, EnableAimAssist,
DoubleTapHoldToIronSightFire, AutoShootCancelsSprinting, SprintCancelsReload, AutoShootSwitcher,
ReturnToIronSightAfterReload, CustomizedHud, EnableNotifications, PlayerFov(80), ControlScheme,
MaxFramerate, GraphicSetting(1)`; `EN/UserPreferencesComponent.cs:28-140`,
`GM/GamePreferencesComponent.cs:20-90`), gamepad mapping (`Action1..4, LeftBumper, RightBumper,
LeftTrigger, RightTrigger, Sprint`; `GM/InputSettingsComponent.cs:76-230`), `alias`, `language`,
`forcedLanguage`, `age`, `gender`, `mm_last_shop_visit`, `tracking_tles`, `tle_status_tracking`,
`lastChatInteractionTime`, `hitOfflineDateS`, current SNS key. These already work offline.

---

## 6. Load pipeline (per player, on the server) **[V]**

Entry: client handshake on a TS/GS connection → `Connection.ProcessHandshake` opens
`SessionForUser` (`EN/Connection.cs:1550-1580`) → delegates `GameServerComponent.Handshake`
(`GM/GameServerComponent.cs:934`) and `AgeGateChecker.Handshake` (`GM/AgeGateChecker.cs:65`) both call
`ProfileManagerComponent.LoadPlayerProfile(connection.Session)` (shared per fed id via `SharedTask`,
`GM/ProfileManagerComponent.cs:956-995`).

HTTP sequence (S = storage, A = auth, C = config/CRM; `me` = the player via the for-user token):

```
 1  S  GET  profiles/me/myprofile/_mcvs.gameProfile                       (storage_ro storage_restricted)
        200 → template JSON (opaque)          404/403 → CreateProfile (4.2):
          A  GET  users/me                    (account info; cached)
          [only if sole credential is anonymous]
          A  POST authorize (game super cred, device "mcvsGameServer") + GET users/me
          C  GET  configs/users/me            → must contain "game" (JObject)
          [skipped for client id 66102 unless CRM overrides UnsupportedLinkFrom]
          A  GET  users?device_id=<dev>       → []
          S  POST profiles/me/myprofile/_mcvs  object={"gameProfile":null}&visibility=restricted
 2  S  GET  profiles/me/myprofile?include_fields=_mcvs.matchers,_mcvs.snapshot,credential,created,
        modified,datacenter,last_session,total_spent,total_transactions,last_purchase,devices,level,xp,
        total_playtime,country,language,birthdate,gender,location,clan,pending_clan,carrier,
        available_transports,current_client_id,current_device,current_device_id,banned_from,
        inventory,_inventory                                              (must be 200 + JSON object)
        for each of _mcvs.matchers / _mcvs.snapshot / inventory / _inventory that is absent:
          S  POST profiles/me/myprofile/<selector>  object={}&visibility=restricted
 3  (ValidateProfileVersion; only for Version==56:
     S  GET /profiles/fed_id%3A<acct>/myprofile/backups?start_date=…&end_date=…  → [] ; …/backups/{id})
 4  if PlayerName empty (new profile):
     A  POST games/mygame/alias   → {"alias":"123456789"}   (REQUIRED – exception aborts the load)
     SOC POST accounts/me  name=agent-<alias>                (errors swallowed)
 5  if !isValid (new/reset):  SAVE (section 7)   else if TS: S POST profiles/me/myprofile/_mcvs.matchers (if modified)
 6  S  GET  profiles/fed_id%3A<acct>/myprofile/_mcvs.currentMatch          (storage_admin, game-server token)
        404 ⇒ ignored (OnlineRequestFailedException caught, GM/GameServerComponent.cs:962-969)
 7  TakeProfileLock: ProfileOwnerRoomId := Room.Id ; SAVE                 (GM/GameServerComponent.cs:1118-1123)
 8  RegisterUser → leaderboard "global" PostScore (swallowed) ; AgeGate: StandardProfile birthdate/country
 9  OnProfileLoaded (server): MercsComponent adds missing mercs; LoadStartingInventory (needs CRM config,
     grants table "StartingInventory" once; GM/PlayerProfileComponent.cs:5541-5571); RetrieveInstallationDate
     (StandardProfile.created); IapStoreComponent.InitAsync → CheckInventory(isInit) (§13.3)
```

`LoadProfile` then instantiates/updates the GameObject from the merged template, sets each
`UserComponentBase.Session`, wires `GameProfileSection`/`MatchersProfileSection`, runs `LoadAntiCheat`
(empty), `InitPlayerName`, `UpdateLoginStats`, saves when needed, `go.FinishCreate()`
(`GM/ProfileManagerComponent.cs:856-903`).

Read-only loads of **other** players (profile screen, `GetProfileRequest`,
`GM/PlayerProfileComponent.cs:6432-6445`): `GET profiles/fed_id%3A<id>/myprofile?include_fields=
_mcvs.gameProfile,credential,…` with the game server token (`GM/ProfileManagerComponent.cs:997-1027`).
Batch reads: `GET /profiles?credentials=a,b&include_fields=_mcvs.snapshot.MatchMakingRating` → JSON array
of nested objects (`GM/GameMatchmakingComponent.cs:1026-1030`) and `include_fields=language,country`
(`GM/GameClanComponent.cs:1436`).

Client-side storage reads (rare): `GET profiles/me/myprofile/_mcvs` to detect "new account" during SNS
linking (404 = new; `GM/GameClientComponent.cs:3076-3088`).

---

## 7. Save pipeline **[V]**

### 7.1 `SaveProfile` gate (`EN/ProfileComponent.cs:63-82`)

Runs `DoSaveProfile` if `EnableSave` and the session is not read-only (or `forceSaveIfPaused` while the
server is paused). `DisableSave()` is called after GS match-end saves and on datacenter moves.

### 7.2 `PlayerProfileComponent.DoSaveProfile` (`GM/PlayerProfileComponent.cs:5252-5296`)

```
ResetAutoSaveTimer
if GameProfileSection == null || PlayerName empty → return            (offline/default profiles never save)
GameProfileSection.FromGameObject(GameObject)                           (full template dump)
UpdateLoginStats(isInitialLogin:false)
if GameProfileSection.IsModified:
   S POST profiles/me/myprofile/_mcvs.gameProfile  object=<dump>&visibility=restricted  [If-Match: etag]
   PrepareStandard (xp, level, total_playtime) ; PrepareSnapshot ; PrepareMatchers (+ A GET users/me)
   S POST profiles/me/myprofile  operation=merge  object={<standard writable fields>,
                                  "_mcvs":{"snapshot":{…},"matchers":{…}}}            (errors swallowed)
raise ProfileSaved ; CRMComponent.RefreshCrmConfig  (C GET configs/users/me)
all exceptions swallowed
```

Because the sections save is a root-level `merge` whose body contains a partial `_mcvs` object, the
backend **must merge recursively**; a shallow merge would replace `_mcvs` and drop `gameProfile` and
`currentMatch`. **[L – inferred from the request shape]**

### 7.3 When saves happen **[V]**

| Trigger | Evidence |
|---|---|
| Autosave every `AutoSaveIntervalMinutes` (default 5) while `AutoSave` | `GM/PlayerProfileComponent.cs:4736-4760` |
| Scene/level loading | `GM/PlayerProfileComponent.cs:4762-4765` |
| Player level change (server) | `GM/PlayerProfileComponent.cs:4266-4282` |
| Handshake: new/reset profile, profile lock | `GM/ProfileManagerComponent.cs:892-894`, `GM/GameServerComponent.cs:1118-1123` |
| TS pause (going to an offline level) / resume / connection close | `GM/GameServerComponent.cs:592-609, 688-704, 735-768` |
| GS match end (all connected players), then `DisableSave` | `GM/GameplayComponent.cs:898-910` |
| Offline bot match result processed (forceSaveIfPaused) | `GM/OfflineBotsGameplayComponent.cs:626` |
| Season end, daily shop refresh, tracking flush | `GM/DivisionsComponent.cs:443`, `GM/DailyShopComponent.cs:393`, `GM/GameTrackingComponent.cs:18298` |
| `ForceSaveProfile` RPC (account link, cheats) | `GM/PlayerProfileComponent.cs:2722-2726, 5304-5314` |
| Datacenter copy | `GM/GameServerComponent.cs:915-930` |

There is **no save on app backgrounding** in the client (`GM/GameClientComponent.cs:3144-3170`). With
an embedded TS living in the app process, killing the app can lose up to one autosave interval of
progress. Mitigations: patch a server-side save on `AppFocusLost/AppMinimized`, or (data-only) lower the
persisted `AutoSaveIntervalMinutes` in the stored profile (**[L]**: it is a public get/set of
`ProfileComponent`, so it is part of the dump and survives the default-merge).

### 7.4 Failure behaviour

* Save errors are swallowed (`DoSaveProfile` catches everything) ⇒ silent data loss. **[V]**
* Load errors propagate to the handshake ⇒ error popup / retry loop. **[V]**
* `CurrentMatchAccessor.Save` parses the body as JSON ⇒ return `{}`, never invalid JSON
  (`GM/CurrentMatchAccessor.cs:128-142`). **[V]**

---

## 8. Conflicts between copies **[V unless noted]**

1. **No client cache.** The client never keeps or uploads a profile copy; the only persistent copy is
   storage; the TS/GS in-memory GameObject is the working copy.
2. **ETag / If-Match** (only `GameProfileSection` and `CheatSection`; `StandardProfile` disables it).
   412 ⇒ `Load()` refreshes the section string but **does not re-apply it to the GameObject**; the next
   save dumps the in-memory object again with the new ETag ⇒ in-memory server state wins one autosave
   later (`GM/GameProfileSection.cs:184-199`). A stub may omit ETags entirely.
3. **Storage wins on resume.** After an offline level the TS calls `ReloadProfileAsync` (re-GET +
   `ToGameObject`) before continuing (`GM/GameServerComponent.cs:735-757`,
   `GM/PlayerProfileComponent.cs:4967-4989`). After a GS match the TS reloads what the GS saved.
   ⇒ storage must be read-your-writes consistent across TS and GS (and across devices in P2P).
4. **Single-session lock** `ProfileOwnerRoomId` (persisted in the dump). On login, if set, the TS posts
   a `multipleConnectionDetected` secured message, kicks the user from that TS room (matchmaker), and polls
   `ReloadProfileAsync` every 1 s for `MultipleConnectionGracePeriodTime` (5 s), then proceeds
   (`GM/GameServerComponent.cs:1134-1173`). Cleared on clean disconnect
   (`SaveAndReleaseProfile`, 694-704). After a crash the next login simply waits ~5 s.
5. **Pending GS reconnect.** `_mcvs.currentMatch.CurrentServerType=="gs"` makes the TS try to reconnect
   the player to that room (`FindRoom`, not wrapped in try/catch) or apply a pending loss
   (`GM/GameServerComponent.cs:1064-1108`). Offline stub: return 404 / `{}` for `_mcvs.currentMatch`
   unless GS reconnect is implemented.
6. **Version guard and migrations** (4.3).
7. **Account conflicts** (SNS link/merge, device auto-merge in `CreateProfile`, transfer codes) – handled
   by auth/social code; irrelevant for a single local anonymous account.

---

## 9. Local files and paths **[V]**

`SaveStream` = `GlfStream` with `LocationType.Save` (`EN/SaveStream.cs`); root from native
`GlfStream_private_GetSavePath()` (`EN/GlfStream_private.cs:154-158`, `EN/GlfStream.cs:49-55, 263`). The
native engine imports Java `RetrieveSavePath` (string present in `extracted/lib/arm64-v8a/libnativeengine.so`)
which returns `getFilesDir()` (`decompiled-java/sources/com/gameloft/android/PackageUtils/AndroidUtils.java:142-144`)
⇒ `/data/data/com.gameloft.android.ANMP.GloftMVHM/files/` **[L for the JNI link]**.

| File (relative to save path) | Content | Evidence |
|---|---|---|
| `MyLocalStorage` | JSON object of settings/flags (5.4); plain text, no encryption/checksum | `EN/LocalStorageComponent.cs:16, 66-110` |
| `datacenter` | chosen datacenter name | `EN/OnlinePlatformComponent.cs:53-55, 202` |
| `importedFriends_<type>_<user>` | JSON array | `EN/SocialComponent.cs:573-594` |
| `Crashes/…` | crash logs | `EN/GameApplication.cs:565, 1286-1287` |
| `CrateSimulation_*.csv` | cheat output | `GM/CheatsComponent.cs:1771` |

No progression, no encryption, no checksums are involved locally. The game profile has no compression
either (plain form field). Templates/tables (`DefaultProfile.tx`, MetaDatabase) are read from the OBB.

---

## 10. What the game already supports offline (and what it doesn't)

| Capability | Status | Evidence |
|---|---|---|
| Boot into a local default profile with no server (`ServerLocation.None` link) | exists (dev path), **not persisted** | `GM/ProfileManagerComponent.cs:835-853`, `GM/PlayerProfileComponent.cs:5255` |
| In-process title/game server (`ServerLocation.Embedded` + LocalAnubis) | exists, but loads/saves via Federation storage over HTTP | `EN/ClientComponent.cs:987-1009`, `EN/Connection.cs:1564`, `GM/GameServerComponent.cs:943` |
| Offline levels: practice range, bot matches, tutorial | exist; results are reported to and saved by the (paused) TS | `GM/GameClientComponent.cs:3731-3755, 4041`, `GM/OfflineBotsGameplayComponent.cs:528-630` |
| Local persistence of progression | **absent** | sections 2, 9 |
| Server-side upgrade/merge/validation of saves | in the game server code itself (ValidateProfileVersion) | 4.3 |

The official environment is gone: eve answers `404 … not configured` for client id
`2620:66102:1.17.32:android:googleplay` (`captures/eve-requests.txt`, `captures/eve-02-urls-mdc.http`),
so existing cloud saves are unreachable; every offline user starts from `DefaultProfile.tx`.

---

## 11. Storage/profile endpoints the stub must serve

All on service `storage` unless noted; tokens per report 02 (query for GET, form for POST). "me" resolves
to the token's subject (the `for_username` player for `SessionForUser` tokens).

| # | Method & path | Request | Response | Needed for |
|---|---|---|---|---|
| S1 | `GET profiles/{me|cred}/myprofile/_mcvs.gameProfile` | – | 200 raw JSON value; **404** if absent | profile load (every TS/GS login, resume) |
| S2 | `GET profiles/{me|cred}/myprofile` | `include_fields=<comma list of dotted paths>` | **200** JSON object containing the requested paths as nested objects (absent paths omitted); `{}` for a new account | sections load |
| S3 | `POST profiles/{me|cred}/myprofile/{selector}` | `object=<json>`, `visibility=restricted`, optional `operation`, optional `If-Match` | **200** (body ignored; `{}` fine); optional `ETag` | game profile save, section creation, `_mcvs` init, `language` field, `currentMatch` |
| S4 | `POST profiles/me/myprofile` | `object=<json>`, `operation=merge`, `visibility={"*":"restricted"}` (standard save) or none (sections save) | **200** | standard + snapshot + matchers save (deep merge) |
| S5 | `GET profiles/fed_id%3A{id}/myprofile/_mcvs.currentMatch` | scope storage_admin | 404 or `{…}` | TS/GS handshake |
| S6 | `POST profiles/fed_id%3A{id}/myprofile/_mcvs.currentMatch` | `object={…}` | 200 `{}` | room registration |
| S7 | `GET profiles/{cred}/myprofile/{selector}` | – | raw value / 404 | `current_client_id`, `_mcvs.snapshot` of others, `_mcvs` probe |
| S8 | `GET /profiles` | `credentials=c1,c2`, `include_fields=…` | 200 JSON **array** of nested objects (one per credential) | MMR of party members, clan PN languages |
| S9 | `POST profiles/me/myprofile/delete` | – | 200 | reset profile (cheat/admin) |
| S10 | `GET /profiles/{cred}/myprofile/backups` (+`/{id}`) | `start_date, end_date` | 200 `[]` | only for profiles with Version 56 |
| A1 | `GET auth users/me` | – | `{"account":…, "credentials":["anonymous:…"]}` | account info in CreateProfile, matchers |
| A2 | `GET auth users?device_id=` | – | `[]` | device merge check |
| A3 | `POST auth games/mygame/alias` | – | `{"alias":"<digits>"}` | **required** for player name ⇒ saves |
| C1 | `GET config configs/users/me` | – | JSON object **with `"game":{…}`** (optionally `"_ServerConfig"`, `"_OverrideTables"`) | profile creation, CRM refresh after each save, starting inventory |
| SOC1 | `POST social accounts/me` | `name, language, country` | 200 | publish player name (non-fatal) |

Semantics to implement in the stub's document store (per account, keyed by fed id):

* **Path model:** selector and `include_fields` entries are dotted paths from the document root
  (`_mcvs.gameProfile`, `inventory`, `language`). Split only these request strings on `.`; stored values
  are opaque.
* **set** (no `operation`): parse `object` as JSON (empty ⇒ JSON null / delete) and place it at the path,
  creating intermediate objects. **merge**: recursive object merge (objects merged key by key; other
  values replaced). **delete**: remove the path. `add/increment/append/batch_*`: not used by the game.
* **visibility**: accept and ignore (or store).
* **ETag**: optional. If implemented, use a per-document version, return `ETag: "<n>"` on GET/POST and
  answer `412` on `If-Match` mismatch (the game reloads). Simplest: never send ETag.
* **Durability:** write atomically (temp file + fsync + rename) before answering 200; keep N rotating
  backups per account (also enables "restore previous save" and reversible unlock-all).
* Maintain `credential`, `created`, `modified`, `last_session` server-side (read-only fields).
* Return `{}`/`[]` rather than empty bodies for JSON-parsed calls.

---

## 12. Recommendation – offline persistence

1. **Implement storage inside the local Federation stub** (same native/HTTP server that answers auth/eve
   per report 02). Back it with `files/mcvs_offline/accounts/<account>.json` (one Seshat document per
   account) plus `…/<account>.json.bak{1..5}`. This needs **no game patch** and keeps the TS/GS code paths
   exactly as shipped (migrations, lock, reload-on-resume, GS match saves).
2. Use the **embedded TS** (`ServerLocation.Embedded`) for the main menu so all meta-game logic runs
   in-process; offline levels (bots/practice) keep working because they report to that TS.
3. Provide the bootstrap answers listed in §11 (alias, users/me, users?device_id, CRM with `"game"`),
   otherwise new profiles cannot be created or never save.
4. Keep `_mcvs.currentMatch` empty unless GS reconnect is supported; optionally have the stub clear
   `ProfileOwnerRoomId` inside the stored dump on startup to avoid the 5 s lock wait after crashes.
5. Data-loss hardening: lower `AutoSaveIntervalMinutes` (data-only, §7.3) and/or patch a server-side
   save on app pause.
6. Offer export/import of `<account>.json` (it is plain JSON, human-editable) for device migration
   (replaces Gameloft transfer codes).

Alternative (not recommended): patch `GameProfileSection`/`ProfileSection` to use `SaveStream` files.
It saves an HTTP hop but leaves every other storage consumer (StandardProfile, snapshot, CRM bootstrap,
currentMatch, batch profiles) to be patched too, and does not help P2P.

---

## 13. Unlock-all: where to hook

### 13.1 Existing server-side helpers (used by the dev cheat menu) **[V]**

All run on the server with a `PlayerProfileComponent` (`GM/CheatsComponent.cs:495-760`):

| Cheat | Implementation | Notes |
|---|---|---|
| UnlockAllMercs | `MercsComponent.CheatUnlockAllMercs(profile)` – unlocks every displayed spec except `"0"` (`GM/MercsComponent.cs:2443-2470`) | needs ShortDefs present (true after `ProfileLoaded`) |
| UnlockAllMercSkins | `MercSkinsComponent.UnlockAllSkins(profile)` (+camo if `LinkCamoToSkin`) (`GM/MercSkinsComponent.cs:192-215`) | unlocked mercs only |
| UnlockAllMercWeapons | `MercWeaponComponent.UnlockAllWeapons(profile)` (`GM/MercWeaponComponent.cs:171-190`) | unlocked mercs only |
| Unlock all weapon camos | private `CheatsComponent.UnlockAllCamos` (`GM/CheatsComponent.cs:3949-3970`) | `WeaponCamosComponent.UnlockAllCamos` requires `profile.IsDeveloper` (`GM/WeaponCamosComponent.cs:252-276`) |
| Unlock all accessories | `CheatsComponent.UnlockAllAccessories` / `UnlockAllMercAccessories` (3917, 1586) | |
| Unlock All Mods | `ModsMetaComponent.GetUnlockedModsListForTiers(id, 5, p)` + `profile.UnlockModForMerc` (1047-1075) | |
| Unlock All Gear Slots | `merc.GearSlots[*].Locked=false` (1385-1394) | |
| AllSignatures | `public static CheatsComponent.AllSignatures(world, profile)` (1159-1174) | |
| MaxCurrencies | `AddCurrency` up to 999999 HC/UpgradeTokens/SeasonTickets/VersusCoins, 500 Credits (1242-1249) | |
| Max Mastery All Mercs | `merc.Level = 23` (1507-1516) | skips mastery rewards |

### 13.2 Recommended hook (small IL patch, Game.dll) **[V for the call targets, design]**

`PlayerProfileComponent.OnProfileLoaded()` (`GM/PlayerProfileComponent.cs:2964-2974`): after
`base.OnProfileLoaded()` (which raises `ProfileComponent.ProfileLoaded` ⇒ `MercsComponent.ProfileLoaded`
has populated all ShortDefs), add on the **server** (`World.Synchronization.IsServer && Session?.IsReadOnly != true`):

```csharp
if (OfflineUnlockAll.IsEnabled(World, this)) {
    var meta = World.GetMetaGame();
    meta.GetComponent<MercsComponent>().CheatUnlockAllMercs(this);
    meta.GetComponent<MercSkinsComponent>().UnlockAllSkins(this);
    meta.GetComponent<MercWeaponComponent>().UnlockAllWeapons(this);
    // camos/accessories/mods/gear slots/signatures: copy the bodies of the CheatsComponent helpers
    CheatsComponent.AllSignatures(World, this);
    World.Synchronization.RequestSynchronization();
    SaveProfile(CancellationToken).FireAndForgetTask(...);
}
```

Toggle sources that need no new UI: the raw launch arguments (`GameApplication.Instance.Args.Args`
contains every `phantomArgs` element, e.g. `-UnlockAll`; `EN/ApplicationArguments.cs:20-27`), or a CRM
game parameter served by the stub (`CRMComponent.CrmConfig.RawGameParameters["OfflineUnlockAll"]`), which
lets a stub settings page flip it. Because grants are persisted, unlock-all is one-way for that save;
make it reversible at the stub level (separate profile slot or automatic backup before first enable).
Patching Game.dll requires dropping/regenerating `libaot-Game.dll.so` (report 02 §7). Hooking ownership
checks instead is not recommended: many code paths read `ShortDef.Locked/Skins/Weapons` directly.

### 13.3 Zero-patch alternatives

* **Developer cheat menu.** `IsValidDeveloper = IsDeveloper && IsDevelopment`
  (`GM/PlayerProfileComponent.cs:1126-1136`). On TS handshake `IsDeveloper` is set from
  `Geolocalization.IsInGameloftEdgeList(clientIp, AdditionalDeveloperIpRange)`, and the edge list
  **always contains `127.0.0.0/8 192.0.0.0/24 10.0.0.0/8`** (`UT/Geolocalization.cs:170-174`,
  `GM/GameServerComponent.cs:978-993`). `IsDevelopment` defaults to `true` in code and is overridden by CRM
  `game._ServerConfig.default.IsDevelopment` (`GM/GameServerComponent.cs:123, 275-291`). With an embedded
  TS (loopback client) the player is very likely a valid developer, and the Flash UI calls
  `CanUseCheats`/`GetCheatsList`/`ActivateCheat` (`GM/CheatsComponent.cs:226-230, 411-450`). **[L]** that
  the UI exposes a cheat button (UI assets are in the OBB). Downside: exposes every dev cheat (reset
  profile, crash test, datacenter switch). Set `IsDevelopment:false` in the stub's CRM to hide it.
* **IAP ledger grant channel.** The TS compares the stored `inventory` section with
  `CrmStandardData.Inventory` in the profile on every login (`IapStoreComponent.InitAsync` →
  `CheckInventory(isInit:true)`, `EN/IapStoreComponent.cs:858-864, 1243-1305`) and on a `set_profile`
  alert (958-969); each positive difference `key → n` is granted as `RewardHelper.CreateRewardItem(key, n,
  IAP_BUNDLE)` via `ClaimRewardOfType(force:true)` (`GM/PlayerProfileComponent.cs:6088-6145`). Keys use
  the reward-type grammar `<Type>_<sub1>_<sub2>…` where `Type` ∈ {`UnlockMerc`, `MercSkin`, `MercWeapon`,
  `MercWeaponFragment`, `WeaponCamo`, `MercAccessory`, `Mod`, `ModTier`, `Gear`, `GearSlot`,
  `KillSignature`, `HardCurrency`, `Credits`, `UpgradeTokens`, `VersusCoins`, `SeasonTickets`, `Shards`,
  `PlayerXP`, `Crate`, …} (`GM/RewardItem.cs:227-250`, `GM/*RewardItem.cs`). So the stub can give items by
  bumping `inventory.<key>` – no patch – but it needs the item IDs from the OBB tables. **[L]** for runtime
  ordering (the profile's `PurchaseCompleted` subscription must be in place first).
* **Stub-side save editing**: the dump is readable JSON; flip `Locked`, extend `Skins`/`Camos`… in
  `$default./Profile.Mercs`. Same ID-catalog requirement, and fragile against template collapsing rules.

### 13.4 "All items obtainable through progression"

Items whose only source was IAP/time-limited events need a new source: CRM `_OverrideTables` (per-user
table overrides of MetaDatabase, `EN/CRMComponent.cs:246-291`) can retune crate pools, shop prices and
starting inventory; the IAP ledger channel above can implement "free purchases" of IAP packs. Details belong
to the CRM/IAP analysis.

---

## 14. P2P implications

* The **GS host loads every participant's profile through its own Federation** and saves all of them at
  match end (`GM/GameServerComponent.cs:943`, `GM/GameplayComponent.cs:898-910`); the guest's own TS then
  reloads from storage on resume. With per-device stubs this means: **a guest's profile must be readable
  and writable by the host's stub, and the guest's stub must see the host's write before the guest's TS
  resumes.**
* Suggested design: make tokens self-describing with the account's *home* stub address (extends report 02
  §0.8), and have each stub **proxy storage requests for non-local accounts** to the owning stub
  (`GET/POST profiles/fed_id:<guest>/…` forwarded over LAN/Internet). The guest's document stays the
  single source of truth; no merge logic needed. Alternatively upload a copy at join and pull it back
  after the match (riskier).
* Host authority over match rewards is inherent to the game design (GS computes them); acceptable for
  friends' sessions, not cheat-proof.

---

## 15. Verified vs inferred (summary)

Verified: all storage request shapes and selectors; section classes and field lists; create/load/save
logic and status-code requirements; deep-merge-dependent save shape; template serialization rules;
migration logic; save triggers; lock and reload behaviour; local files; absence of local progression
persistence; offline-link default profile; cheat helpers; edge-list loopback inclusion; IAP ledger diff
and grant path.

Likely: `GameProfileSelector == "_mcvs"`; sibling component list; save path = `getFilesDir()`; Seshat
merge is recursive; UI exposes the cheat menu when `CanUseCheats` is true; IAP grant ordering at login;
`AutoSaveIntervalMinutes` edit takes effect from the stored dump.

Speculative: child names other than `/Profile` in the dump; `ProfileManagerComponent.ProfileVersion` = 57;
exact `DefaultProfile.tx` contents.

---

## 16. Open questions

1. `DefaultProfile.tx` (OBB) – exact component list/names, default currencies/mercs, `ProfileVersion`,
   `AutoSaveIntervalMinutes`; needed to build a stub-side save editor or ID catalog.
2. Which client link templates use `ServerLocation.Embedded` vs `Online` for the TS, and how to force
   Embedded (template/phantomArgs) – multiplayer/LocalAnubis analyst.
3. Embedded TS remote endpoint type (loopback TCP?) – determines `IsDeveloper` and whether
   `IsInGameloftEdgeList(null)` could throw in the handshake.
4. CRM (`configs/users/me`) minimal content for `CRMComponent.CrmConfigAsync` so that
   `LoadStartingInventory`, IAP store init and TLE/seasons work – CRM analyst.
5. MetaDatabase format and item ID tables (mercs, skins, camos, accessories, mods, signatures) – data
   analyst; needed for data-only unlock-all.
6. Leaderboard/TLE emulation for seasons/divisions and daily quests (otherwise those progress tracks are
   frozen offline).
