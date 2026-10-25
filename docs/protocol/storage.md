# Protocol notes: STORAGE

Owner group: **STORAGE** (Storage).
Scope: Seshat profile storage (`profiles/...`, `data/...`).
Evidence: verify-C3 (normative); report 03; report 02 §9. Code: `server/Services/Storage/`.
Tests: `tests/McvsOffline.Tests/Storage/`.

Seshat is the game's only progression store. The TS and GS Worlds load and save each player's profile here
through `SessionForUser` (super credential, `for_username`). Nothing is saved on the device by the game itself,
so this module decides whether progress survives (verify-C3 §1, §5).

## Endpoints

All paths are relative to the host root. `{cred}` is `me` (the token's subject), `fed_id:<id>` /
`fed_id%3A<id>` (also a doubly encoded `%253A`), or any other `type:user` credential. `{sel}` is a dotted
selector (`_mcvs.gameProfile`). Every endpoint needs a known `access_token` (query for GET, form for POST, both
for DELETE; a `Bearer` header and a JSON body also work). Otherwise it answers **401**.

| Method | Path template | Route name | Response | Evidence | Status |
|---|---|---|---|---|---|
| GET | `profiles/{cred}/myprofile` `[?include_fields=a,b.c]` | `storage.profile.get` | 200. Whole document or a nested projection; `{}` for an account that never saved | verify-C3 §3.2 (R2) | done |
| POST, PUT | `profiles/{cred}/myprofile` `object=, operation=, visibility=` | `storage.profile.write` | 200 `{}` (R5 `operation=merge`: deep merge) | verify-C3 §2.3 R5, §7.4 | done |
| GET | `profiles/{cred}/myprofile/{sel}` `[?include_fields=]` | `storage.field.get` | 200. The exact stored text, or `{}` if absent | R1, R8, verify-C3 §3.1, §3.4 | done |
| POST, PUT | `profiles/{cred}/myprofile/{sel}` `object=, operation=, visibility=` | `storage.field.write` | 200 `{}` | R3, R4, R6, R7, R9, R11 | done |
| DELETE | `profiles/{cred}/myprofile/{sel}` | `storage.field.delete` | 200 `{}` | report 02 §1.4 | done |
| POST | `profiles/{cred}/myprofile/delete` (also `DELETE profiles/{cred}/myprofile`) | `storage.profile.delete` | 200 `{}`; the document is removed and a backup is kept | OP/StorageApi.cs:156, GM/GameProfileSection.cs:131 | done |
| GET | `profiles/{cred}/{name}/backups?start_date=&end_date=` | `storage.backups.list` | 200 `[{id,date,level,size,totalSpend,index,revision,reason}]` (our real backups, newest first) | R10, OP/StorageService.cs:14-40 | done |
| GET | `profiles/{cred}/{name}/backups/{id}` | `storage.backups.get` | 200. The backed-up document, or `{}` for an unknown id | GM/ProfileManagerComponent.cs:131-160 | done |
| GET | `profiles/me/myprofile/visibility` | `storage.visibility.get` | 200 `{selector: visibility}` | OP/StorageApi.cs:253-269 | done (unused by game) |
| POST | `profiles/me/myprofile/visibility[/{sel}]` `visibility=` | `storage.visibility.set` | 200 `{}` | same | done (unused by game) |
| GET | `profiles?credentials=c1,c2&include_fields=` | `storage.profiles.batch` | 200 `[...]`, one projection per credential in request order (`{}` if unknown) | GM/GameMatchmakingComponent.cs:1029, GM/GameClanComponent.cs:1436 | done |
| GET | `profiles/location/{cred}` | `storage.profiles.nearme` | 200 `[]` | OP/StorageApi.cs:271 | stub (unused) |
| GET | `profiles/matchers/{name}/matches` | `storage.profiles.matches` | 200 `[]` | OP/StorageApi.cs:234 | stub (unused) |
| GET | `data/{cred}/{key}` | `storage.data.get` | 200 raw `data` text; 404 if absent | OP/StorageApi.cs:165 | done (unused: BinaryProfileSection has no subclass) |
| POST | `data/{cred}/{key}` `data=, visibility=, category=, expiration=, indexes=, meta_data=` | `storage.data.write` | 200 `{}` | OP/StorageApi.cs:171 | done (unused) |
| POST | `data/me/{key}/delete` | `storage.data.write` | 200 `{}` | OP/StorageApi.cs:226 | done (unused) |
| GET | `data?category=` | `storage.data.search` | 200 `[entries without data]` for the caller | OP/StorageApi.cs:192 | done (unused) |
| GET | `_mcvs/storage` | `storage.diag.list` | 200 `{directory, profiles:[{account, credentials, revision, created, modified, hasGameProfile, size, backups}]}` | diagnostics | loopback only |
| GET | `_mcvs/storage/profiles/{account}` `[?raw=1]` | `storage.diag.profile` | 200. The document as the game sees it (pretty), or the stored file | diagnostics | loopback only |

### The game's request sequence (verify-C3 §2.3) and our answers

| # | Request | Answer |
|---|---|---|
| R1 | `GET profiles/me/myprofile/_mcvs.gameProfile` | the dump byte-for-byte; `{}` when there is none. We never answer 404/403 here, so the game never takes the `CreateProfile` path (auth + CRM calls and the R7 `_mcvs` reset). |
| R2 | `GET profiles/me/myprofile?include_fields=_mcvs.matchers,_mcvs.snapshot,credential,...,inventory,_inventory` | 200. A **nested** object (`{"_mcvs":{"matchers":…}}`, never `"_mcvs.matchers"` keys); `{}` for a new account |
| R3 | `POST …/<_mcvs.matchers \| _mcvs.snapshot \| inventory \| _inventory>` `visibility=restricted&object={}` | 200 |
| R4 | `POST …/_mcvs.gameProfile` `visibility=restricted&object=<dump>` (+ `If-Match` only if an ETag was received: never) | 200, no ETag |
| R5 | `POST profiles/me/myprofile` `operation=merge&object={<standard fields>,"_mcvs":{"snapshot":…,"matchers":…}}` | 200, deep merge keeps `gameProfile` and `currentMatch` |
| R6 | `POST …/_mcvs.matchers` (full section, Set) | 200 |
| R7 | `POST …/_mcvs` `object={"gameProfile":null}` | 200; replaces `_mcvs`. A backup of the previous state is taken first (it wipes the dump). |
| R8 | `GET profiles/fed_id%3A<id>/myprofile/_mcvs.currentMatch` (game-server token) | stored object, or `{}`. **Never 404**: the GS would dereference a null accessor (verify-C3 §3.4). |
| R9 | `POST profiles/fed_id%3A<id>/myprofile/_mcvs.currentMatch` `object={…}` | 200 `{}` (parsed as JSON by the caller) |
| R10 | `GET /profiles/fed_id%3A<id>/myprofile/backups?...` (only for Version 56 profiles) | 200 array of our backups. 1.17.32 writes Version 57, so the game never asks. |
| R11 | `POST …/language` `object="en"` | 200 |
| R12 | `install_verified_status` (only with `-PerformIapCheck`, off by default) | ordinary field get/set |

## Semantics

**Document model.** One document per account, keyed by account id (`fed_id` = `fed_id:<account>`). Selectors and
`include_fields` entries are split on `.` only. Stored keys may contain dots, slashes, `@` and `$`; they are just
not addressable by path. Non-negative integer segments index arrays on reads.

**Writes** (`SeshatOperations`, verify-C3 §7):
- *set* (no `operation`). The value replaces the one at the selector and intermediates are created. The value is
  stored **verbatim**: a selector GET returns exactly the text that was sent, and the value is embedded unchanged
  in larger responses and in the file. This matters most for `_mcvs.gameProfile`, whose Templates dump has
  collapsed dotted keys and number spellings (`1e5`, `3.140`, `-0`, huge integers, `é`) that a
  re-serializer would change. Writing *inside* a verbatim value turns it into a normal value first.
- An empty `object=` deletes the selector (that is what `DeleteProfileField` sends).
- JSON `null` is stored, except at `_mcvs.snapshot`, `_mcvs.matchers`, `inventory` and `_inventory`, where it
  deletes. Read back as `"null"`, those sections make `SetProfile("null")` throw (§7.5). The same rule applies
  to nulls that arrive in a merge.
- *merge*. Objects merge recursively; arrays, scalars and null replace. A merge at a selector that holds no
  object sets the value.
- *delete* removes the selector (the root deletes the document).
- *add / increment / append / batch_set / batch_delete* are not used by the game. They do the obvious thing:
  numeric add (also recursively over objects), set-union on arrays, array append, `{path: value}` sets, and path
  list deletes.
- An unknown operation changes nothing and answers 200 `{}`. It is logged at WARN with the full request.
- An `object=` that is not valid JSON is stored as opaque text: returned unchanged by a selector GET, and a JSON
  string inside documents.
- Read-only standard fields (`credential, created, modified, datacenter, last_session, total_spent,
  total_transactions, last_purchase, banned_from`) are server-maintained. Writes to them are ignored and logged.
  Reads of a stored document get `credential` (the account's first credential), `created`, `modified`,
  `last_session` (the last saved change) and `datacenter` (settings), placed first.
- `visibility` is recorded per selector (the root form `{"*":"restricted"}` is merged key by key) and never
  enforced.
- `If-Match` is ignored. **No ETag is ever sent**: with a per-document ETag every later game-profile save would
  fail with 412 forever (§3.5).
- Writing the same text again changes nothing (no file write, no revision bump).

**Status codes.**

| Situation | Status |
|---|---|
| Successful write | **200** `{}` |
| Successful read | **200** |
| Absent selector | 200 `{}`. This is required for `_mcvs.gameProfile` and `_mcvs.currentMatch`, and fine elsewhere; `storage.absentField = "404"` switches other selectors to 404 |
| Missing or unknown token | **401** (the client drops its tokens and re-authorizes once) |
| Unreadable stored file with no usable backup, I/O error | **503** (retried 5× by the client). We never answer "missing", which would reset the profile |
| Malformed request the game never sends (bad selector `a..b`, root set with a non-object, invalid credential) | 400 |
| **403** | **never** (403 = "not found" = profile wipe, §3.1) |
| **404 for existing data** | **never** |

**Authorization.** Any valid token may read and write any account. The game server's own token must reach other
players' `_mcvs.currentMatch` (`storage_admin`, §7.7), and the read-only `SessionForUser` uses the server's
token with `fed_id` paths. The only possible refusal would be a 403, which the game reads as a missing profile.
Scopes are not checked.

**Credential resolution.** `me` is the token's subject: for the game server's `for_username` tokens, the player.
`fed_id:<id>` is the account id directly. Other credentials are looked up in the account store. Reads of an
unknown credential see an empty profile; writes create the account (`AccountStore.GetOrCreate`, same
deterministic id the auth module derives).

## Persistence

- One file per account: `<data>/storage/profiles/<account>.json` (key encoded with `DocumentStore.EncodeKey`).
  It is a readable, hand-editable JSON envelope:
  `{format, version, account, revision, created, modified, visibility, verbatim:[{path, kind}], profile}`. Verbatim
  values are written inline as real JSON. On load their exact text is recovered with `JsonSpanScanner` and
  checked against the parsed value (a mismatch only costs verbatim-ness, never data). Hand edits, even
  reformatting, are picked up. A plain JSON object without the `format` marker (for example an exported Seshat
  profile) is imported as the document.
- **Atomic and durable.** Every change is written as temp file + fsync + rename (`AtomicFile`) *before* the
  answer, so a crash leaves the old or the new file, never a partial one. Leftover temp files are removed at
  start.
- **Ordering.** Every operation on an account runs under that account's **FIFO** lock (`FairKeyedLock`), so
  requests are applied in arrival order and every read sees all earlier writes. This is the TS/GS hand-off
  requirement of §4.
- **Cache.** Parsed documents are kept in memory per account and checked against the file's length and
  last-write time on every request, so a hand edit is picked up as soon as it is saved. Each request gets its
  own copy, with shared immutable leaves: the dump is neither copied nor parsed per request, and it is parsed
  only when a read goes inside it. Without the cache, a 1.4 MB dump cost about 210 ms per request (even an R8
  `currentMatch` read) on the dev machine, and more on a phone. `storage.cache: false` turns it off.
- **Backups** in `<data>/storage/backups/<account>/<yyyyMMddTHHmmssfffZ>-r<revision>-<reason>.json`:
  - `auto`: before a save when the newest backup is older than `storage.backups.intervalMinutes`;
  - `delete`: before a profile delete;
  - `replace`: before a root set;
  - `wipe`: before any write that empties a stored game profile, such as the R7 reset.
  The newest `storage.backups.keep` are kept. The backups also back the Seshat backups endpoint.
- **Corruption.** If a stored file exists but cannot be parsed, it is moved aside (`*.corrupt-<ms>`) and replaced
  by its newest readable backup, with an ERROR log. With no backup, requests answer 503 and the file is left
  untouched for repair. A broken file is never treated as a new profile.

## Settings (`settings.json`)

```json
"storage": {
  "backups": { "keep": 20, "intervalMinutes": 30 },
  "autoRestoreCorrupt": true,
  "absentField": "empty",
  "cache": true
}
```

| Key | Default | Meaning |
|---|---|---|
| `storage.backups.keep` | 20 | backups kept per account; 0 disables backups |
| `storage.backups.intervalMinutes` | 30 | minimum minutes between two `auto` backups; 0 = before every save |
| `storage.autoRestoreCorrupt` | true | restore an unreadable profile from its newest backup |
| `storage.cache` | true | keep parsed profiles in memory (validated against the file on every request) |
| `storage.absentField` | `"empty"` | `"404"` answers 404 for absent selectors, except `_mcvs.gameProfile`, `_mcvs.currentMatch` and the four object sections, which stay `{}` |

## Seams for other modules

- **`IProfileStore` / `IProfileStoreLocator`** (LAN stage, roadmap M3, verify-C6 §3 topology B, report 03 §14).
  Each request resolves its `StorageTarget` (account id, addressed credential, calling token). It then asks
  `context.Features.Get<IProfileStoreLocator>()` for a store, and falls back to the local store when there is
  no locator or it returns null. A LAN forwarder implements `IProfileStore` by relaying the same request to the
  account's home device and returning that device's status and body unchanged. The guest's own server then stays
  the single source of truth. The locator is consulted on every request, so it may be registered later.
- **`IProfileAccess`** (`context.Features.Get<IProfileAccess>()`): `HasProfile` (any write creates a profile, even an
  R3 section creation), `HasGameProfile` (a non-empty `_mcvs.gameProfile`, i.e. the TS's R4 happened),
  `Read`/`ReadText`, `Set`, `Merge`, `Remove`. Same locking, durability and backups as game requests.
  - `AddToInventory(account, rewardKey, delta)` is the **IAP ledger grant channel**. The TS diffs `inventory`
    against what it already granted, on every login and on a `set_profile` alert, and grants the difference as an
    IAP_BUNDLE reward (report 03 §13.3; EN/IapStoreComponent.cs:1243-1305).
  - Counts are kept as integers in int range: the TS reads `(int)(long)JValue.Value`.
  - `AddToLedger(account, "_inventory", ...)` does the same for the ads ledger.
- **`LocalProfileStore`** is also registered as a feature (diagnostics, a future import/export tool).
- **Events** (`context.Events`):

| Topic | Payload | Publisher → typical subscriber |
|---|---|---|
| `storage.profile.changed` | `{account, fedId, revision, operation, selector, deleted, source: "http"\|"api"}` | storage, after every saved change → anything that mirrors profiles (LAN sync) |
| `storage.inventory.changed` | `{account, fedId, section, key, count, delta}` | storage `IProfileAccess.AddToLedger` → alerts (push a `set_profile` alert so a running TS re-checks the ledger without a re-login, EN/IapStoreComponent.cs:958-969) |

## Decisions

- **`{}` rather than 404 for an absent game profile.** It gives the same new-profile result as 404 without the
  `CreateProfile` path: no auth/CRM dependency, and no R7 reset that would wipe `_mcvs` (verify-C3 §3.1).
- **Verbatim storage of every selector set**, not only `_mcvs.gameProfile`. One uniform rule, faithful round
  trips everywhere, and no number drift in matchers or snapshot either.
- **Read-only fields are synthesized, not stored.** The game never writes them, and `created` feeds
  `RetrieveInstallationDate`.
- **A validated cache instead of reading every request from disk.** The file stays the source of truth: it is
  written before every answer, and the cache is dropped whenever the file's stamp changes. A cache miss costs
  about two parses of the dump (parse plus verbatim check).
- **The backups endpoint serves our real backups** instead of the `[]` suggested by verify-C3. It is harmless:
  the game only uses backups whose dump has `Version < 56`, and it is useful for tools.
- **Own file layout instead of `context.OpenStore`**, because of the envelope, raw-text access, FIFO ordering and
  backups. It still uses the core's `AtomicFile`, `FileSystem` and `DocumentStore.EncodeKey`.
- **Batch profiles answer one entry per requested credential** (`{}` for unknown ones), so array indexes match
  the request.

## Open questions / device-test checklist

- Mono `Uri` and `%3A`: we accept `fed_id:`, `fed_id%3A`, `fed_id%3a` and `fed_id%253A`. Confirm on device which
  form arrives (look for `storage.field.get` lines in `server.log`).
- `Expect: 100-continue` with large dumps: tested against our server over sockets. Confirm on device.
- Real dump sizes, and the time per R1/R4 on the device (the debug log has per-request ms).
- Confirm that the TS/GS hand-off never reads a stale profile after a bot match: the fire-and-forget save versus
  the resume GET (verify-C3 §4). We apply requests in arrival order; the residual race is in the game.
- The first login after install: R1 `{}` → default profile → save. Check that no 4xx appears in the log on the
  path.
- Whether anything calls `profiles/me/myprofile/_mcvs` from the client to detect a new account
  (`IsNewAccount`, SNS link flows only). With `{}` it reads as "not new". Set `storage.absentField = "404"` if a
  link flow needs the other answer.
