# Verification C3: progression storage (Seshat profile doc, TS/GS writers, stub contract)

**Verdict: PARTIALLY CONFIRMED.**

The architecture in the claim holds. No progression is saved on the device. The profile is one Seshat "myprofile" document per account, read and written only by server Worlds (TS and GS) through `SessionForUser` over HttpClient. A stub that keeps one JSON document per account can give persistent progression without a game patch.

The stub contract as written in the claim and in report 03 §11 has problems:

* Some details are wrong, and two of them cause permanent data loss.
* The list of save triggers includes a dead one and leaves out real ones.

The stub must follow the corrections in §7, or saves fail silently.

Paths are relative to `/home/user/mcvs-work`:

* `OP/` = `decompiled/Gameloft.OnlinePlatform/Gameloft.OnlinePlatform/`
* `EN/` = `decompiled/Gameloft.Engine/Gameloft.Engine/`
* `GM/` = `decompiled/Game/Game/`
* `UT/` = `decompiled/Gameloft.Util/Gameloft.Util/`

I read every cited path myself. I did not take any line number from the reports.

**Datapack.** `datapack/1.17.32/ETC/files/Data/` is present. It has 15,747 files and may be incomplete. Its files are GCBF/LZ4 containers. I decoded three of them with a small decoder I wrote (`scratchpad/c3/gcbf.py`; scratchpad = `/tmp/claude-0/-home-user/98d9334c-9d5f-5995-bde1-c27c1652af38/scratchpad`):

* `Data/MCVS.game`
* `Data/1/52218da.f` (the ProfileManager template)
* `Data/c/fa4981c.f` (very likely `DefaultProfile.tx`: it has `/Profile`, `/League`, `/Divisions`, `/Gears`, `/DailyShop`, `/SubscriptionCard`, `/DailyReward` and `/ProfilePropertyTable`; I did not resolve the name-to-hash mapping)

---

## 1. What is confirmed

| Sub-claim | Status | Evidence |
|---|---|---|
| Selector is `_mcvs` | **Confirmed (data)** | `datapack/.../Data/MCVS.game` → `"/GameDefinition": {"GameProfileSelector": "_mcvs", …}`. Code default `"game"` at `EN/GameDefinitionComponent.cs:204`; hard-coded `jObject["_mcvs"]["gameProfile"]` at `GM/ProfileManagerComponent.cs:137,160` |
| Game profile path `_mcvs.gameProfile` | Confirmed | `GM/GameProfileSection.cs:100-103` |
| Only server Worlds load and save | Confirmed | Load: `GM/GameServerComponent.cs:943`, `GM/AgeGateChecker.cs:71`. Save gate: `GM/ProfileManagerComponent.cs:883-898` (`IsServer`). Autosave: `GM/PlayerProfileComponent.cs:4736-4742` (`World.IsServer()`) |
| Server uses `SessionForUser` (super credential, for the player) | Confirmed | `EN/Connection.cs:1564` → `EN/AuthenticationComponent.cs:576-583` |
| Save = full Templates dump as `object=` on `_mcvs.gameProfile` | Confirmed | `GM/GameProfileSection.cs:105-109`; `GM/PlayerProfileComponent.cs:5258-5265`; `OP/ProfileSection.cs:194-230` |
| Then one root `operation=merge` with standard, snapshot and matchers | Confirmed | `GM/PlayerProfileComponent.cs:5266-5283`; `OP/ProfileSection.cs:342-391` |
| Root GET for a new account must be 200 with a JSON object | Confirmed | §3.2 |
| Saves must be exactly 200 | Confirmed (with nuance, §3.3) | `OP/ProfileSection.cs:218-221, 377-380` |
| Deep merge is needed | Confirmed | `OP/ProfileSection.cs:349-364` builds a partial `{"_mcvs":{"snapshot":…,"matchers":…}}` at root |
| No local progression save | Confirmed for C# (§5) | — |
| `ServerLocation.None` never saves | Confirmed (§5.2) | — |
| GS also writes the profile | Confirmed (§4) | `GM/GameplayComponent.cs:898-910` and more |
| Embedded TS "does the same over HTTP" | **Consistent, not fully verified here** | No server code path uses non-HTTP storage. Federation uses `HttpClient(FedRequestHandler(HttpClientHandler))` (`OP/Federation.cs:468-472`). I did not re-verify how the embedded server World is brought up (reports 06/09). |
| Target `ProfileVersion` = 57 | **Confirmed (data)**; report 03 had it as [S] | `datapack/.../Data/1/52218da.f`: `"/ProfileManager": {"UserObjectTemplate":"DefaultProfile.tx","ProfileVersion":57}` |
| `AutoSaveIntervalMinutes` = 5 | Confirmed | Code default `EN/ProfileComponent.cs:48`; `fa4981c.f` `/Profile.AutoSaveIntervalMinutes: 5` |

Extra fact from the data: `DefaultProfile` has `"Version": 4, "GameVersion": "1.5.4"`. A new or empty profile therefore always takes the "num < 52 → full reset" branch, and the code sets `Version = 57` afterwards (`GM/ProfileManagerComponent.cs:185-192, 307-329`).

---

## 2. Wire format (verified, client side)

### 2.1 Transport

* **GET.** Parameters go in the query, including `access_token` (added only if the URI has no query) (`OP/FedRequestMessage.cs:164-171, 196-204`).
* **POST.** Parameters go in the body as `application/x-www-form-urlencoded; charset=utf-8` (`OP/RequestParameters.cs:14-16`, `OP/FedRequestMessage.cs:173-177`).
* **Encoding.** Values are encoded with `Uri.EscapeDataString` in 4096-character chunks (`OP/MoreUri.cs:8-23`), so spaces become `%20`, never `+`.
* **Headers.** Every request carries `Cache-Control: NO-CACHE` and a custom User-Agent (`OP/FedRequestMessage.cs:137-142`).
* **URL resolution.** The base URL is `FormatUri(locatorResult)`, with the trailing `/` stripped. The request URL is then `new Uri(new Uri(base), Path)` (`OP/FedRequestMessage.cs:220-222`, `OP/Federation.cs:420, 456-465`). Two consequences:
  * A relative `profiles/...` path **replaces the last segment of the service URL**. If the locator returns `http://h:p/storage`, the request goes to `http://h:p/profiles/me/myprofile/...`. Leading-`/` paths (`/profiles`, `/profiles/{cred}/myprofile/backups`) are host-absolute.
  * `FormatUri` forces the default protocol (HTTPS) and drops the port when the port is 80, 443 or absent. Hosting the stub on plain HTTP needs a non-standard port.
* **Status handling.**
  * Any non-2xx except 302 becomes `OnlineRequestFailedException` carrying the response (`OP/Federation.cs:231`, `OP/HttpResponseMessageExtension.cs:49-69`).
  * 5xx and 408 are retried, up to 5 attempts.
  * 401 refreshes the token once.
  * Any other code aborts (`OP/Federation.cs:495-556`, `OP/RequestHandler.cs:46-125`).
* **Credential in the path.** `me`, or `Uri.EscapeDataString("fed_id:<id>")` → `fed_id%3A<id>` (`OP/Credential.cs:107-124`). Mono's `Uri` may or may not keep `%3A`, so the stub should accept both `%3A` and `:` (device test).

### 2.2 Builders (`OP/StorageApi.cs:98-136`)

```csharp
// GetProfileRequest
string text = $"profiles/{credential.ToUrlEncodedString()}/myprofile";
if (!string.IsNullOrEmpty(selector)) text = $"{text}/{selector}";
... if (includeFields != null && includeFields.Count > 0) Parameters["include_fields"] = string.Join(",", includeFields);
// SetProfileRequest
fedRequestMessage.Method = HttpMethod.Post;
if (etag != null) fedRequestMessage.Headers.IfMatch.Add(etag);
if (operation != Operation.Set) Parameters["operation"] = OperationName[operation];
if (visibility.HasValue) Parameters["visibility"] = (string.IsNullOrEmpty(selector) ? $"{{\"*\":\"{vis}\"}}" : vis);
fedRequestMessage.Parameters["object"] = profileString;     // null → "object=" (RequestValue(null).StringValue = "")
```

### 2.3 Every storage request on the progression path

| # | Who and when | Request | Response the game needs |
|---|---|---|---|
| R1 | TS/GS load (`GM/ProfileManagerComponent.cs:975`) | `GET profiles/me/myprofile/_mcvs.gameProfile` (scope `storage_ro storage_restricted`) | 200 + stored JSON text, kept **opaque** as a string (`GM/GameProfileSection.cs:31-53`). ETag optional. "Missing" may be **404, or 200 with `{}`, `null` or an empty body** (§3.1) |
| R2 | Load (`GM/ProfileManagerComponent.cs:980-987`, `OP/ProfileSection.cs:269-290`) | `GET profiles/me/myprofile?include_fields=_mcvs.matchers,_mcvs.snapshot,credential,created,modified,datacenter,last_session,total_spent,total_transactions,last_purchase,devices,level,xp,total_playtime,country,language,birthdate,gender,location,clan,pending_clan,carrier,available_transports,current_client_id,current_device,current_device_id,banned_from,inventory,_inventory` (order not guaranteed, from a HashSet) | **200 + a JSON object**, with dotted paths returned as **nested** objects. Read with `SelectToken(path)` (`OP/ProfileSection.cs:310, 318`) |
| R3 | Load, once per absent selector section | `POST profiles/me/myprofile/<_mcvs.matchers \| _mcvs.snapshot \| inventory \| _inventory>` `visibility=restricted&object={}` | **exactly 200**; anything else fails the load (`OP/ProfileSection.cs:326-333` → 218-221) |
| R4 | Save, game profile (`GM/PlayerProfileComponent.cs:5265`) | `POST profiles/me/myprofile/_mcvs.gameProfile` `visibility=restricted&object=<dump>` + `If-Match: <etag>` only if an ETag was received; no `operation` (Set) | **exactly 200**, body ignored. Response `ETag` is stored (`OP/ProfileSection.cs:223`) |
| R5 | Save, sections (`GM/PlayerProfileComponent.cs:5278`) | `POST profiles/me/myprofile` `operation=merge&object={<non-RO standard fields>,"_mcvs":{"snapshot":{…},"matchers":{…}}}`; **no visibility, no If-Match**. Only modified sections are included. Absent standard fields go out as JSON `null` (`OP/ProfileSection.cs:365-370`) | exactly 200 (errors swallowed: `GM/PlayerProfileComponent.cs:5276-5282`) |
| R6 | TS load, valid profile (`GM/ProfileManagerComponent.cs:895-898`); `SetKayacVersion` (`GM/PlayerProfileComponent.cs:2567, 2572`) | `POST profiles/me/myprofile/_mcvs.matchers` (and `_mcvs.snapshot`) `visibility=restricted&object=<full section>` (Set) | exactly 200. A failure inside `SetKayacVersion` propagates into the AgeGate handshake |
| R7 | New-profile path, if R1 = 404/403 (`GM/GameProfileSection.cs:181`) | `POST profiles/me/myprofile/_mcvs` `visibility=restricted&object={"gameProfile":null}` (Set on **all** of `_mcvs`) | any 2xx (`OP/StorageService.cs:95-98`) |
| R8 | TS/GS handshake (`GM/GameServerComponent.cs:964`, `GM/CurrentMatchAccessor.cs:110-126`) | `GET profiles/fed_id%3A<id>/myprofile/_mcvs.currentMatch` with the **game-server** token, scope `storage_admin` | 200 + JSON object or empty body. **Do not return 404** (§3.4) |
| R9 | GS room close / match result / client private match (`GM/GameServerComponent.cs:411`, `GM/PlayerProfileComponent.cs:4835`, `GM/GameClientComponent.cs:1448,1463`) | `POST profiles/fed_id%3A<id>/myprofile/_mcvs.currentMatch` `object={…}` (Set, no visibility) | 2xx with an **empty or valid-JSON body** (parsed by `OP/Session.cs:237-246`) |
| R10 | Load, only if `Version==56` (`GM/ProfileManagerComponent.cs:170-178`) | `GET /profiles/fed_id%3A<id>/myprofile/backups?start_date=…&end_date=…`, then `…/backups/{id}` | 200 `[]` (an error here fails the load) |
| R11 | Server-side language (`EN/LocalizationComponent.cs:235`) | `POST profiles/me/myprofile/language` `object="en"` | 2xx |
| R12 | Only with `-PerformIapCheck` (`EN/Connection.cs:1519-1545, 1569-1572`) | `GET …/install_verified_status.result` must be `1`; then `POST …/install_verified_status` `object=` | off by default (`EN/GameApplication.cs:150`) |

---

## 3. Behaviour on missing data, errors and ETags

### 3.1 Missing game profile

**404 works, but it is not required, and it brings in extra dependencies.**

On 404 **or 403**, `LoadJson` calls `CreateProfile` (`OP/ProfileSection.cs:129-136, 161-172`). `GameProfileSection.CreateProfile` (`GM/GameProfileSection.cs:143-182`) then does the following:

1. `GET auth users/me`.
2. If the only credential is anonymous, it opens a new super session (`"mcvsGameServer"`) and reads CRM. The cast `(JObject)((JObject)cfg.JsonConfig["game"])["_ServerConfig"]` throws an **uncaught `NullReferenceException` when `"game"` is absent**; only `OnlineRequestFailedException` is caught (`:65-73`).
3. For client id `…:66102:…`, the default `UnsupportedLinkFrom` list (`:23`) skips the device-merge loop.
4. It runs R7.

`ProfileString` stays null, so `GetTemplate()` returns null and the default profile is used (`:117-127`).

The alternative is to answer 200 with `{}`, `null` or an empty body. `SetProfile(text)` stores the string and `GetTemplate()` returns null for whitespace, `"null"` or `"{}"`. That gives the same new-profile result without R7 and without the auth and CRM calls. Either way, `ValidateProfileVersion(null)` returns `isValid=false` (`GM/ProfileManagerComponent.cs:185-192, 307-323`), and the server then saves immediately (`:891-894`).

**Data-loss trap.** 403 is treated exactly like 404. If the stub ever answers 403 on R1 for an existing account (scope or ACL check, wrong token), the TS:

* runs R7, which **wipes `_mcvs`**: snapshot, matchers and currentMatch;
* loads a fresh default profile and saves it over the real one.

On R2 a 403 creates `{}` for inventory, snapshot and the other selector sections, then fails on `StandardProfile.SetProfile("")`. **The stub must answer 401 for authentication problems, never 403 or 404 on a read.** It must also never serve a half-written file, because a transient 404 has the same effect.

### 3.2 Root GET (R2) must be 200 with a JSON object

`LoadSections` parses the body with `Json.LoadObject` (`OP/ProfileSection.cs:337-340`, `UT/Json.cs:52-57`).

On 404 or 403, `jsonProfile` stays null:

* The selector sections are created (R3).
* `StandardProfile`, a field-list section, gets `text = ""` (`OP/ProfileSection.cs:307-329`). `SetProfile("")` then calls `JObject.Load` on empty input, which throws `JsonReaderException`, and `LoadPlayerProfile` fails.

Confirmed. Returning dotted keys flat, as `{"_mcvs.matchers":…}`, would make `SelectToken` miss them. The game would then re-create, and blank, `matchers`, `snapshot`, `inventory` and `_inventory`. The response must be nested.

### 3.3 "Exactly 200"

These callers throw on a status other than `OK`:

* `ProfileSection.SaveImplementation` (R4, R3, R6)
* `SaveSections` (R5)

`SetProfileField` (R7, R11) and `CurrentMatchAccessor` (R9) accept any 2xx.

How the error surfaces depends on the caller:

* **Swallowed** inside `DoSaveProfile` (`GM/PlayerProfileComponent.cs:5258-5288`): silent data loss.
* **Fatal** during load (R3) and in `SetKayacVersion` (R6).

### 3.4 `_mcvs.currentMatch`: return 200 `{}`, not 404

`LoadForUser` only catches `OperationCanceledException` (`GM/CurrentMatchAccessor.cs:110-126`). A 404 therefore propagates and is swallowed in the handshake, which leaves `currentMatchAccessor = null` (`GM/GameServerComponent.cs:961-969`). The GS branch then dereferences it:

```csharp
// GM/GameServerComponent.cs:1102-1112
if (IsGS && Room != null && Room.IsClosed()) {
    if (Room.IsGameOver()) await playerProfile.ApplyAndResetPendingLoss(currentMatchAccessor, ct);   // NRE on null
    else { currentMatchAccessor.ReconnectAttemptsCount = 0; await currentMatchAccessor.Save(ct); } // NRE on null
```

This NRE fires whenever a player reconnects to a GS whose room is already closed (the match has started). Report 03 §8.5 and §12.4 suggest "return 404 / {}" for currentMatch. **Use 200 `{}`, or an empty body**, which yields `new CurrentMatchAccessor(…, new JObject())`.

Storing currentMatch faithfully is also safe. A stale `"gs"` entry leads to `FindRoom`, which catches every exception (`EN/ServerComponent.cs:612-640`), and then to `ApplyAndResetPendingLoss` + `Reset` (`GM/GameServerComponent.cs:1068-1100`, `GM/PlayerProfileComponent.cs:5403-5415`). The result is an abandon loss, which is the original behaviour.

### 3.5 ETag and 412: the report-03 recommendation is a permanent-save-failure trap

Only `GameProfileSection` uses `If-Match`. `StandardProfile.Etag => null` (`OP/StandardProfile.cs:96`). `SaveSections` sends none and does not update any section's ETag (`OP/ProfileSection.cs:373`). On 412, the base save throws, and `GameProfileSection` then calls `Load()`, which re-GETs storage text and ETag, and **returns normally**. The GameObject is not re-applied (`GM/GameProfileSection.cs:184-199`). `DoSaveProfile` then continues and sends R5.

Report 03 §11 says: "If implemented, use a **per-document** version … and answer 412 on If-Match mismatch". With a per-document version, this sequence follows:

```
GET gameProfile            -> ETag v1
R3 / R5 / R6 / R11 writes  -> doc v2..vk          (no If-Match, they always succeed)
POST gameProfile If-Match v1 -> 412 -> Load() -> ETag vk  (dump NOT stored)
POST root merge (R5)       -> doc vk+1
next save: POST gameProfile If-Match vk -> 412 -> ...    (forever)
```

The game profile is then never written again. Every save cycle performs a root merge, so the per-document version always moves on between two game-profile saves.

**Rule for the stub:** send no ETag at all (simplest; `Etag` stays null and no `If-Match` is sent), or derive the ETag from the `_mcvs.gameProfile` value alone. If an ETag is sent, it should be a quoted entity-tag. How Mono's header parser handles an unquoted value is unverified (device test).

### 3.6 Version migration (`GM/ProfileManagerComponent.cs:180-337`)

Confirmed as described in report 03 §4.3, with these additions:

* A profile with `Version==56` and **no backups** is rolled back to `LoadDefaultProfile`. `Version` is then 4, which is below 52, so the profile is **fully reset** (`:148-167, 170-178`). Only saves imported from a 10.12-era build are affected. 1.17.32 writes 57.
* A missing or edited `Version` also resets the profile.
* A saved `GameVersion` whose features are newer than the running build throws `ClientGameVersionIsGreaterThanServerGameVersionException` (`:267-270`, fatal in `EN/Connection.cs:1590`).
* The loaded template is merged onto a fresh `DefaultProfile.tx` (`:330-335`).

The stub does not have to migrate anything. It must store the dump verbatim.

---

## 4. TS and GS coordination (the claim mixes up two mechanisms)

`ProfileOwnerRoomId` (public get/set, so it is persisted; `GM/PlayerProfileComponent.cs:1516`) is a **TS-only** lock:

* `TakeProfileLock` runs only when `IsTS` (`GM/GameServerComponent.cs:970-973, 1091`).
* `MakeSureProfileIsUnlocked` is reached only from TS paths (`:1118-1123`, and `ResumeServer`, which returns early on the GS at `:737-740`).

It guards against a second TS session: another device, or a stale lock left by a crash. In that case the TS:

1. posts `multipleConnectionDetected`;
2. kicks the old TS room (errors caught);
3. calls `ReloadProfileAsync` every 1 s for up to `MultipleConnectionGracePeriodTime` = 5 s (`:137, 1134-1166`);
4. proceeds anyway and calls `EnableProfileSave()` (`:1172`).

**The GS never reads or takes this lock.** TS and GS coordinate through the following sequence (`ClientComponent.ConnectToServer`, `EN/ClientComponent.cs:1095-1111`). Offline links go through the same pause at `:1097-1102`.

1. The client calls `PauseCurrentLink`, then awaits the TS `PauseServer` reply. In `GameServerComponent.DoPauseServer`:
   * `SaveAndReleaseProfile`: lock set to null, **save**, `DisableSave()`;
   * `AutoSave=false`;
   * `MainProfileComponent` sets `EnableSave=false` (`GM/GameServerComponent.cs:694-704, 759-768`; `EN/MainProfileComponent.cs:180-197`).
2. The GS handshake runs `LoadPlayerProfile` with a fresh GET (R1/R2). The GS sets `AutoSave=true` (`GM/GameServerComponent.cs:975`), so it can autosave during the match.
3. At GS match end, every connected player is saved and then `DisableSave()` is called (`GM/GameplayComponent.cs:898-910`). `CloseConnectionAsync` → `SaveAndReleaseProfile` also saves, unless saving is already disabled (`GM/GameServerComponent.cs:592-609`).
4. On TS `ResumeServer`:
   * `AutoSave=true`;
   * `ReloadProfileAsync`: R1 again and `ToGameObject`, so **storage overwrites the TS's in-memory copy** (`GM/PlayerProfileComponent.cs:4967-4977`);
   * `MakeSureProfileIsUnlocked`, which re-enables saving;
   * lock set;
   * save (`GM/GameServerComponent.cs:735-757`).

Consequence: the stub must be **strictly read-your-writes per account, with requests applied in arrival order**. The offline-bot result is a special case. It is applied on the paused TS and saved **fire-and-forget**:

```csharp
// GM/OfflineBotsGameplayComponent.cs:626
profile.SaveProfile(profile.CancellationToken, forceSaveIfPaused: true).FireAndForgetTask(...);
```

The client sends the result as a plain control message (`:401-415`). If the client's `ResumeServer` re-GET (step 4) reaches storage before that POST has been applied, the reload restores the pre-match state, and the next save makes the loss permanent. The stub should serialize per account and fsync before answering. A residual race remains even so (device test).

---

## 5. Local saves and offline links

### 5.1 No local progression

Every `SaveStream` user is in `EN/LocalStorageComponent.cs` (file `MyLocalStorage`, `:19, 68-107`), `EN/SocialComponent.cs`, `EN/OnlinePlatformComponent.cs`, `EN/GameApplication.cs` or `GM/CheatsComponent.cs`.

The `LocalStorage.Set` keys are:

* `alias`, `age`, `gender`
* input bindings
* `UserPreferences` properties
* TLE tracking
* `lastChatInteractionTime`
* the current SNS key

Other `File.*` writes are debug CSVs and tools. None of these is progression.

The Java `SharedPreferences` users are SDKs (IAB, push, ads, Adjust, Fabric). `libnativeengine.so` embeds a Gaia/glwebtools layer with `saveGame` and `SaveGameInfo` strings, but no C# reference to it exists (grep over `decompiled/`). The account identity itself (the anonymous username and password from native `AuthenticationComponent_GetAnonymousUsername/Password`) is local, outside the scope of this check. If it changes, the stub document is orphaned.

### 5.2 `ServerLocation.None` never saves

`ClientLink.Offline => ServerLocation == None` (`EN/ClientLink.cs:124`). `ProfileManagerComponent.OnUpdate` loads `DefaultProfile.tx` with `gameProfile = null` and `matchersSection = null` (`GM/ProfileManagerComponent.cs:843-849`). `GameProfileSection` is never assigned (`:878-882`). `DoSaveProfile` returns at `GM/PlayerProfileComponent.cs:5255`, and autosave additionally requires `IsServer()`.

---

## 6. Save triggers: corrected list

| Trigger | Status | Evidence |
|---|---|---|
| Autosave every `AutoSaveIntervalMinutes` (5) on TS and GS while `AutoSave` | ✔ | `GM/PlayerProfileComponent.cs:4736-4760`, called from `OnConstantUpdate` `:2961`. `AutoSave` is `[TemplateIgnore(true)]` (`:1468-1478`) and is set true at handshake and resume. Saves only if the dump changed (`OP/ProfileSection.cs:197-201`) |
| **Scene or level loading** (report 03 §7.3) | **✘ dead code** | `Scene_LoadingScene` (`GM/PlayerProfileComponent.cs:4762-4765`) is never subscribed. A Game.dll IL dump has no `ldftn` of it, only its definition |
| New or reset profile at load; TS lock | ✔ | `GM/ProfileManagerComponent.cs:891-894`; `GM/GameServerComponent.cs:1118-1123` |
| TS pause, resume and connection close | ✔ | `GM/GameServerComponent.cs:688-704, 735-768` |
| GS match end (then `DisableSave`) | ✔ | `GM/GameplayComponent.cs:898-910` |
| Offline bot result (forced, fire-and-forget) | ✔ | `GM/OfflineBotsGameplayComponent.cs:626` |
| Player level change | ✔ | `GM/PlayerProfileComponent.cs:4276-4282` |
| Season start, daily-shop refresh, `DoNotShowToday` flag, account link | ✔ | `GM/DivisionsComponent.cs:443`, `GM/DailyShopComponent.cs:393`, `GM/PlayerProfileComponent.cs:2625, 2722-2726` |
| **Purchases** | Partial | Only IAP-bundle earnings, once their tracking is acknowledged (`GM/GameTrackingComponent.cs:18293-18298`). Soft-currency shop buys and crate openings have **no immediate save** (full caller list: `grep SaveProfile(`); they wait for the next autosave or transition |
| App pause or background | ✘ none | Client handlers only call `EnsureConnectionReady` (`GM/GameClientComponent.cs:3144-3170`). No server-side profile save is tied to `AppMinimized` or `AppFocusLost` |

Saves are serialized, never dropped (`UT/NonConcurrentTask.cs:16-28`). With an embedded TS, killing the app loses up to about 5 minutes of meta progress (shop and crates). Matches are safe, because they save at their end.

Minor correction to report 03 §0.5: when the alias request (`POST auth games/mygame/alias`) errors, the load aborts. A 200 without an alias gives `PlayerName = "agent-"`, which is non-empty, so saves still work (`GM/PlayerProfileComponent.cs:2313-2316, 6375-6385`; `OP/Session.cs:614-623`). The report's "no name ⇒ no save ever" is therefore imprecise.

---

## 7. What a stub must reproduce exactly (data-loss checklist)

1. **Read-your-writes and ordering.** Apply requests per account in arrival order, and finish the atomic write (temp + fsync + rename) before answering 200. Never expose a partial file, which would read as 404 and trigger §3.1.
2. **Status codes.**
   * Writes: `200` exactly.
   * Reads of an absent path: `404`, or `200 {}` (`{}` is preferred for `_mcvs.gameProfile` and **required for `_mcvs.currentMatch`**).
   * Authentication failures: `401`, **never 403**, because 403 counts as "not found" and leads to a wipe and reset.
   * Root GET: always 200 with an object.
3. **No ETags**, or ETags per `_mcvs.gameProfile` value only. Never a per-document version (§3.5).
4. **Deep merge on `operation=merge`.** The R5 body carries a partial `_mcvs`. Keep `gameProfile` and `currentMatch`. Arrays and scalars are replaced. JSON `null` is set as null; that is harmless for standard fields.
5. **Set semantics.** `object=<json>` replaces the value at the dotted path and creates intermediate objects. R7 replaces the whole `_mcvs`. Treat an empty `object=` as delete; do not store JSON `null` for the section paths `_mcvs.snapshot`, `_mcvs.matchers`, `inventory` and `_inventory`. Read back as `"null"`, those values make `SetProfile("null")` throw in `JObject.Load` (`OP/ProfileSection.cs:80-90`).
6. **Selectors.** Split only the request selectors and `include_fields` on `.`. Return nested objects. Store `_mcvs.gameProfile` **byte-for-byte**. Its inner keys contain `.`, `/`, `@`, `$` and collapsed dotted names (e.g. `"Attachments.'0'"` in `fa4981c.f`). `IsModified` normalizes JSON, so re-serializing only costs extra saves, but storing the string verbatim avoids number and format drift.
7. **Identity and paths.**
   * `me` means the token subject (the `for_username` player).
   * `fed_id%3A<id>` and `fed_id:<id>` both address an account.
   * Game-server tokens must be allowed to read and write other players' `_mcvs.currentMatch` (`storage_admin`).
   * Inventory values must be integers (`EN/IapStoreComponent.cs:1259`).
8. **Transport.**
   * Accept large form bodies (the full dump, URL-encoded).
   * Handle `Expect: 100-continue` (Mono's HttpWebRequest default; device test).
   * POST responses must be empty or valid JSON (`OP/Session.cs:242-246`).
   * Serve storage where relative-path resolution lands (§2.1).
9. **Supporting endpoints.** These are needed only on the 404 path: `auth users/me`, and CRM `configs/users/me` **containing `"game"`**. Answering `200 {}` for a missing game profile avoids both. `backups` must return `200 []`.

---

## 8. What remains unverified, or outside "storage"

* How the embedded TS World is brought up: super credential, `ServerType="ts"`, and which Federation endpoint it uses (reports 06/09). I verified only that no storage code path bypasses HttpClient.
* Mono-specific behaviour:
  * `%3A` canonicalization in `Uri`;
  * parsing of invalid ETag headers;
  * `Expect: 100-continue`;
  * TLS trust when the locator returns HTTPS.
* "Full progression" also depends on services other than storage:
  * division and season results come from leaderboard `SelfData` (`GM/DivisionsComponent.cs:364-446`);
  * TLE events come from CRM;
  * clans come from the social service.

  These do not hold the profile, but they freeze those progress tracks if they are not stubbed.
* P2P: a GS on another device loads guests through *its* Federation. If that stub answers 404 or 403 for a guest, the GS runs R7 against that stub, which wipes the guest's `_mcvs` there. It then saves a default profile at match end. Guest storage must be proxied to the guest's own stub, not faked.
* `fa4981c.f` = `DefaultProfile.tx` is a strong inference from its content; the hash-to-name mapping was not resolved.
