# Verification of claim C4-bot-match-progression

**Claim (as given):** Free bot matches (`StartMatchWithBots` / `OfflineBotsGameplayComponent`, run locally with
`ServerLocation.None`) pay only the first `BotMatchesInfo` row after PvP activation (no trophies, merc XP, MVP).
GS PvP matches fill empty slots with bots (`EnableBotInLobby` / `FillWithBots` / `CloseRoom` to 8). Therefore, in
Embedded mode, setting CRM `_ServerConfig` `Matchmaking` `MinPlayers=1` and `MaxOpenTimeInSeconds=0` gives a single
human a bot-filled PvP match on the in-process GS, with full progression rewards and no code change.

**Verdict: PARTIALLY confirmed.** Both reward paths and the bot-fill mechanism are as described. I followed each one
in code, including the LocalAnubis Lobby side, and checked the shipped template and DB values in the data pack.
The conclusion needs these corrections:

1. **"Full progression" is not quite full. MMR (Glicko) never changes in a 1-human room.** Bots are never room
   members, so the opposing team passed to Glicko is empty. `FuseTeamStats` then returns false and the MMR delta is
   skipped. Everything else does apply: career trophies, division trophies, league, currencies, MVP, merc XP,
   stats, achievements and the leaderboard post (§2.3).
2. **The CRM change is a convenience, not a requirement.** The shipped `SceneComponents.tx` sets `MinPlayers: 2`
   and `MaxOpenTimeInSeconds: 35`. With those values a lone human still gets a 1+7 match: a lobby bot arrives
   after about 15 s, and `CloseRoom` runs about 35 s later, so the match starts after roughly 50 s. The CRM values
   make it immediate (§3.4).
3. **It only applies after PvP activation.** While `PVPActivated == false`, "Battle" is turned into a local
   onboarding bot match on the TS and never reaches the GS (§3.1).
4. **There are preconditions on the stub and environment that the claim leaves out.** If the GS's own Lobby
   round-trips fail, the GS cannot start at all (null `Room` dereference in `InitWorld`). If a round-trip fails
   *during* `CloseRoom`, the match still fills with bots, but the end-of-match code throws and **no rewards are
   granted or saved** (§4). Also required:
   * lobbyconfigs for `gs` must return `server_type: "gs"`;
   * the gs `cmd_args` must not carry `-Level`;
   * CRM `configs/users/me/game` must answer 200 for the GS session;
   * `auth/verify.account` must equal the profile's fed id.

No anti-farming rule targets bot-filled or low-human matches. The only limiters are the per-division daily
currency caps, which also apply to real PvP (§5).

Path conventions (relative to `/home/user/mcvs-work`): `GM/` = `decompiled/Game/Game/`, `MM/` =
`decompiled/Game/Matchmaking/`, `EN/` = `decompiled/Gameloft.Engine/Gameloft.Engine/`, `LA/` =
`decompiled/Gameloft.Engine/Gameloft.Engine.LocalAnubis/`, `OP/` = `decompiled/Gameloft.OnlinePlatform/Gameloft.OnlinePlatform/`.

**Data pack.** `datapack/1.17.32/ETC/files/Data` is present (15,752 files on disk). I decoded single files in
memory with the existing GCBF decoder (`tools/datapack/gcbf.py`), using a reader script in my scratchpad. Nothing
was written into `datapack/`. Files used:
* `Data/1/52218da.f` = `SceneComponents.tx` (crc32 `152218da`);
* `Data/d/a188f69.f` = `Design/MCVS_Meta.xlsx`, stored as JSON tables;
* `Data/b/de76cd6.f` = `Config/crmpreconfiguration.json`.

---

## 1. Flavour A: free bot match (`OfflineMatchWithBots`). Confirmed.

### 1.1 Rewards are pre-rolled on the TS from `BotMatchesInfo` row 1

`GM/OfflineBotsGameplayComponent.cs:260-266` (TS, `PrepareOfflineMatchOnServer`):
```csharp
if (rewards != null && (profile.BotMatchesAvailable || flag))
{
    winRewards = rewards.Win(profile, countCurrencyInDailyRewardsNow: false,
        MatchConnectionType == GameClientComponent.MatchConnectionType.OnlineAndTutorial,   // isInTraining
        isOfflineMatch: true, isPrivateMatch: false, null);
```
For the bot tile, `isInTraining` is false, so `RewardComponent.Win` takes the `isOfflineMatch` branch
(`GM/RewardComponent.cs:239-241`, and `Lose` at `:336-338`). That branch calls `GenerateOfflineMatchRewards`,
which uses `GetBotMatchInfo`, and that method takes only the first row:

`GM/MetagameComponent.cs:1117-1120`
```csharp
public OfflineRewardInfo GetBotMatchInfo(PlayerProfileComponent profile)
{   return GetUserData(profile.FedId).botMatchRewardsInfos.First(); }
```
Data pack values (`Design/MCVS_Meta.xlsx`):
* `BotMatchesInfo` has 3 rows, all pointing at `BotMatch1WinRewards` / `BotMatch1LossRewards`.
* `BotMatch1WinRewards` = `Credits 5..5`.
* `BotMatch1LossRewards` = `Credits 0..0`.

So a stock post-activation bot match pays **5 Credits on a win, 0 on a loss**, clamped to the division daily cap
(`GM/RewardComponent.cs:396-428`).

### 1.2 PvP activation gates trophies, MVP and merc XP

Client side, `OnEndOfMatch` (`GM/OfflineBotsGameplayComponent.cs:384-394`): only the `!PVPActivated` branch calls
`ApplyTrophiesResult` and computes `isMVP`. The `else` branch sends an empty `agentScores` dictionary and
`isMVP: false`.

TS side, `ProcessRewards` (`GM/OfflineBotsGameplayComponent.cs:477-516`):
```csharp
bool flag = !profile.PVPActivated;
...
if (isMVP && flag) list.AddRange(RewardComponent.GenerateRewardsForMVP(profile));   // :481
...
if (!flag || !base.World.Synchronization.IsServer) return;                          // :505  -> no AddMercXpForScore
```
`OnControlMessageReceived` (`:557`, `:603-622`) applies `ApplyTrophiesResult(giveDivisionTrophies:false)`, and
possibly `ActivatePVP`, only when `flag2 = !profile.PVPActivated`.

**Nuance:** "pays only" holds for rewards, but bot matches after activation still advance some progress:
* `BaseAchievementsComponent.OnMatchResultOffline` trackers: Wins, WinsInARow, WinWithEnemyScoreBelow30/50
  (`GM/BaseAchievementsComponent.cs:635+`, called at `GM/OfflineBotsGameplayComponent.cs:592-598`);
* `MatchesPlayedUsingMerc`, `AllTimeOfflineVictories` and `BotMatchesDoneInTheDay`.

`BotMatchesAvailable` defaults to true (`GM/PlayerProfileComponent.cs:642`). The daily reset sets it back to true
(`GM/MetagameComponent.cs:1160`), and nothing sets it to false.

---

## 2. Flavour B: GS PvP match (bot-filled). Confirmed, except MMR.

### 2.1 Who gets processed

`GM/GameplayComponent.cs:765-929` (`OnMatchOverAsync`, server branch). The reward loop runs over
`multiplayerComponent.ConnectedPlayers` (`:815`), defined as `Players.Where(x => x.HasConnection)`
(`GM/MultiplayerComponent.cs:242`).

Bots have no `Connection`, so they are skipped. Each bot also gets:
* its own credential from `CredentialFromBotName` (`GM/MultiplayerComponent.cs:1201`);
* its own profile GameObject under `BotProfilesRoot`, cloned from the human's stored `ProfileString`
  (`GM/GameProfileSection.cs:117-127`);
* `DisableSave()` on that profile (`:1230`).

So bot profiles cannot overwrite the human's save.

### 2.2 What the human gets (not private match)

| Effect | Code | 1 human + 7 bots |
|---|---|---|
| Currency (Credits + VersusCoins by division, daily caps) | `ProcessRewards`, `GM/GameplayComponent.cs:1129-1170`, `GenerateCurrency` `GM/RewardComponent.cs:342-380` | yes |
| `CratesTrackWinsCount++` | `GM/GameplayComponent.cs:1144` | yes |
| MVP bonus | `isMVP && !isPrivateMatch`, `:1160-1163` (MVP = `MatchTopPlayersManagerComponent` top players) | yes, if the human out-scores the bots |
| Merc mastery XP | `BuildGameResultRewardsObject` → `BuildAgentsMasteryRewards` → `AddMercXpForScore`, `GM/PlayerProfileComponent.cs:5611-5614, 5634-5680` | yes |
| Achievements (full variant), session history, win/loss streak, per-map counts | `GM/GameplayComponent.cs:850-866`, `GM/PlayerProfileComponent.cs:4786-4840` | yes |
| Stats (`Wins`, `Kills`, `Deaths`, `Headshots`, `MVPs`, `Eliminations`, `AllTimePVPMatches`) | `GM/GameplayComponent.cs:868-883` | yes |
| Career (gentle) trophies, division trophies, so league/division progress | `ApplyMatchResult` → `ApplyTrophiesResult(giveDivisionTrophies:true)`, `:1051` | **yes, but only if `profile.FedId` is a key of `matchCompositionDict` (§2.3)** |
| **MMR (Glicko)** | `:1018-1050` | **no (§2.3)** |
| Save + leaderboard post | `:899-916` | yes |

### 2.3 Trophies are keyed on room membership, and MMR is frozen

`GM/GameplayComponent.cs:982-997`:
```csharp
if (matchCompositionDict == null)
{   GameServerComponent gameServerComponent = base.World.Server as GameServerComponent;
    matchCompositionDict = GetPendingLosses(gameServerComponent.Room); }      // :989
if (matchCompositionDict.ContainsKey(profile.FedId))                         // :991
{   ... ApplyMatchResult(profile, matchComposition, ...); }                   // -> MMR + ApplyTrophiesResult
```
`GetPendingLosses` (`:1088-1127`) iterates `room.Members` only (`:1099`). Bots fill free slots through
`Reserve("bot")` reservations and `SpawnBot`. In LocalAnubis, reservations are not members
(`OP/Room.cs:394-395`: `MemberCount = Members.Count`, reservations are counted separately). So with one human:
* `teamA = [human]`, and `teamB = []` (or the reverse);
* `glicko.UpdatePlayerSameDeltaForEveryTeammate(myTeam, enemyTeam=[], ...)` returns false at
  `MM/Glicko.cs:279` (`if (!FuseTeamStats(enemyTeam, ...)) return false;`);
* `flag = (GetNoobSettings(profile)?.AffectMMR ?? flag) && flag` stays false, so there is no MMR delta;
* `ApplyTrophiesResult(..., giveDivisionTrophies: true)` still runs (`:1051`).

The official servers behaved the same way for solo-vs-bots rooms.

Consequences:
* Bot difficulty follows the human's MMR. Bots clone the profile and get `ApplyBotDifficultyFromTrophies(MMR)`
  (`GM/MultiplayerComponent.cs:1178-1181`). Since MMR is frozen, difficulty stays where the profile started.
* If an imported profile already has MMR ≥ `TerminatorThreshold` (2400), every solo match will apply
  "terminator" difficulty to the all-bot enemy team (`GM/GameServerComponent.cs:432-455`). That would be permanent,
  because MMR can no longer move.

---

## 3. Matchmaking settings and the 1-human start

### 3.1 Prerequisite: "Battle" reaches the GS only after PvP activation

`GM/GameClientComponent.cs:3973-3993`: `StartAutoMatchAsync` first asks the TS through `RequestOfflineBotsMatch`.
For "Battle" (`OnlineAndTutorial`), `ShouldPlayAgainstBots` returns `!profile.PVPActivated`
(`GM/OfflineBotsGameplayComponent.cs:271-283`). The client calls `ConnectGameplayServer(QuickMatchServerLink)`
(`:3989`) only when the answer is "no bot match". So a fresh profile first plays local onboarding bot matches
until the league leaves `IsFTUE` and `ActivatePVP` runs (`:603-616`). The BOTMATCH tile always passes
`OfflineMatchWithBots` (`:3963-3966`), so it **never** reaches the GS.

### 3.2 Where the values come from, and the exact CRM location

Code defaults (`GM/GameMatchmakingComponent.cs:65-69, 89`): `MaxOpenTimeInSeconds` = 0 (no initializer),
`MinPlayers` = 2, `MaxPlayers` = 8, `EnableBotInLobby` = true.

Shipped template (`SceneComponents.tx`, decoded `Data/1/52218da.f`, lines 346-354):
```json
"/Matchmaking": { "@type": "GameMatchmakingComponent", "Automatcher": "pvpdeathmatch", "ServerType": "gs",
                  "MinPlayers": 2, "MaxOpenTimeInSeconds": 35, "MaxTags": 2, "Timeout": 10, "Enabled": true }
```
CRM override (`GM/GameMatchmakingComponent.cs:845-883`). It runs on every server World (TS and GS) from `OnStart`
(`:293-299`):
```csharp
JObject obj = await cRMServerSettingsComponent.GetGameParametersAsync(ct);
JObject jObject = obj?.Value<JObject>("Matchmaking");                                   // :854
MaxOpenTimeInSeconds = jObject.Value<int?>("MaxOpenTimeInSeconds") ?? MaxOpenTimeInSeconds;   // :857
MinPlayers = jObject.Value<int?>("MinPlayers") ?? MinPlayers;                           // :858
```
`GameParameters` are built in `EN/CRMServerSettingsComponent.cs:105-127` as follows:
1. The GS opens its super-credential session and calls `GET configs/users/me/game` on the Configuration service
   (`OP/CrmApi.cs:7-19`, scope `config storage_ro`).
2. It takes the top-level `_ServerConfig` object of the response.
3. It starts from `_ServerConfig.default` and merges `_ServerConfig.<GameApplication.ClientId>` (or the client id
   with `. : ,` replaced by `_`) on top.

The exact JSON is therefore:
```json
// body of GET configs/users/me/game  (== CRM document path game._ServerConfig.default.Matchmaking)
{ "_ServerConfig": { "default": { "Matchmaking": { "MinPlayers": 1, "MaxOpenTimeInSeconds": 0 } } } }
```
The bundled `Config/crmpreconfiguration.json` has **no** `_ServerConfig` key. Its `game` keys are `AgeGateParams`,
`CrashlyticsParams`, `_OverrideTables`, `expiry` and `tags`, so the stub must serve this block itself.

The TS also reads `Matchmaking` and uses `NoobSettings` to build automatch filters
(`GM/GameMatchmakingComponent.cs:1060-1070`).

**The CRM fetch is mandatory for the GS anyway.** `GetGameParametersAsync` waits forever if the request fails
(`EN/CRMServerSettingsComponent.cs:33-61`), and the GS awaits it twice:
* in `GetStartLevelArguments` (`GM/GameServerComponent.cs:1194` → `GM/GameplayCRMComponent.cs:75`);
* in the GS Handshake (`GM/GameplayCRMComponent.cs:123`).

So `configs/users/me/game` must return 200, or no GS match starts at all. A side effect: the fire-and-forget
`OverrideFromCRM` cannot in practice lose its race with the first human connection.

### 3.3 Start trigger and bot fill, with no hard-coded minimum besides `MinPlayers`

`HasEnoughPlayerToStartMatch` (`GM/GameMatchmakingComponent.cs:200-210`) is
`ActivePlayersCount >= MinPlayers`, or private match, or editor. It is polled each frame by
`CheckGameplayInitialized` (`GM/GameplayComponent.cs:460-467`). That method latches once and fires
`OnHasEnoughPlayerToStartMatchHandler`, which runs
`CloseRoomAfterDelay(TimeSpan.FromSeconds(MaxOpenTimeInSeconds))` on a non-offline world
(`GM/GameServerComponent.cs:294-310`).

On the headless GS, `IsOffline` is false (`GM/MultiplayerComponent.cs:298-316`, headless and not client).
`IsActive` counts the human only after the connection is `Opened` or `Handshaking`
(`GM/PlayerComponent.cs:172-186`). The PlayerComponent itself is created on `ConnectionOpened`
(`EN/ServerComponent.cs:566-577`), so with `MinPlayers=1` the start fires as soon as the human's handshake
completes.

`CloseRoom` (`GM/GameServerComponent.cs:345-430`), Room path:
```csharp
Room = await GetRoomInfo(Room, ct);                                        // :351
... await UpdateRoom({_closed:1, _bots, _humans, ...}); ReserveBot x AvailableSlots;
Room = await GetRoomInfo(Room, ct);                                        // :379
int num4 = gameMatchmakingComponent.MaxPlayers - Room.MemberCount;         // :382  -> 8 - 1 = 7
... RemoveExtraHumans(); int num5 = UpdateBotsWithRoomInfo();
while (multiplayer.Bots.Count() < num4 + num5) gameMatchmakingComponent.SpawnBot();   // :391-393
BalanceTeams(); ... pendingLosses = World.GetGameplay().GetPendingLosses(Room);       // :394-396
```
Fallback when there is no Room: `obj.FillWithBots(obj.MaxPlayers); BalanceTeams();` (`:420-425`). Both paths sit
inside `try { … } catch (Exception) { }` (`:427`).

Notes:
* The Room path spawns through `SpawnBot` directly and **ignores `EnableBotInLobby`**. Only `FillWithBots`
  checks it (`GM/GameMatchmakingComponent.cs:795-805`).
* `BalanceTeams` gives 4v4 (`GM/BaseSquadSelectorComponent.cs:55-80`): the human plus 3 bots against 4 bots.
* The LocalAnubis Lobby implements every call used here (`LA/Lobby.cs:63-72`): `get room info` (`:518-527`),
  `update room` (`:476-491`), and `reserve` / `remove reservation` (`:529-551`).
* The automatch-created room includes the human as a member: the `Owner` setter calls `AddMember`
  (`OP/Room.cs:128-146`; room built at `LA/Lobby.cs:330`). So `num4 = 7`.
* `AutomatchWaitLaunch` defaults to true (`LA/AutomatchSettings.cs:20`), which launches the room at once
  (`LA/Lobby.cs:358-361`). `game_started` therefore stops the Lobby's idle-room reaper (`LA/Lobby.cs:98-130`).

### 3.4 Timing, with and without the CRM change

Lobby-time fill (`GM/GameMatchmakingComponent.cs:327-361`):
`num = min(floor(elapsed / MaxLobbyTime * MinPlayers), MaxPlayers)`, then `FillWithBots(num)`. It stops once
`Room.IsClosed()` (`GM/RoomExtension.cs:10-16`, `_closed == 1`).

`MaxLobbyTime` comes from `NoobSettings`, else the `MatchmakingBotAddingTime` table, else 60 s. Data pack table
`MatchmakingBotAddingTime`: `[[500,30],[1000,30],[1500,40],[2000,50],[2500,60],[3000,90]]`. With the lookup at
`GM/GameMatchmakingComponent.cs:634-651`, MMR ≤ 1000 gives 30 s.

* **Stock (2 / 35):** the first lobby bot arrives at about 15 s, which satisfies `MinPlayers=2`. `CloseRoom` runs
  35 s later, about **50 s** after the first `GetLobbyInfo`, and tops the room up to 8. So the bot-filled match
  needs **no CRM change**; the claim's settings only remove the wait.
* **CRM (1 / 0):** `CloseRoom` runs as soon as the human's connection opens. With `MinPlayers=1` the lobby-time
  formula adds no bot before 2×`MaxLobbyTime`, so **all 7 bots come from `CloseRoom`**. The client then sees
  `isReady` and runs the 5 s `TeamReview` (`TeamReviewDurationInMilliseconds` can shorten it).
* `AutomatchEmbeddedDelayMS: 10000` on the quickMatch link is read nowhere. Its only use is a copy at
  `EN/ClientSettings.cs:361`, so it adds no delay.

---

## 4. Failure modes the claim leaves out

1. **`LobbyOperation` swallows every exception and returns null** (`EN/ServerComponent.cs:734-754`).
   * If the GS cannot log in to its Lobby at creation, `RefreshRoom` returns null (`EN/ServerManager.cs:186-201`).
     `GameServerComponent.InitWorld` then dereferences `Room` without a check
     (`GM/GameServerComponent.cs:1310` `if (Room.HasAttribute("_arena") …`). The client is never added, so there
     is no match at all. This fails loudly.
   * **Worse: if the first `GetRoomInfo` in `CloseRoom` (`:351`) fails, it sets `Room = null`.** `CloseRoom` falls
     back to `FillWithBots(8)` and the match plays normally. But `matchCompositionDict` was never filled, so at
     match end `ApplyMatchResult` calls `GetPendingLosses(null)`, and `room.Members` at
     `GM/GameplayComponent.cs:1099` throws `NullReferenceException`. That happens inside the first loop of
     `OnMatchOverAsync` (`:779-788`), before `ProcessRewards`, `BroadcastMatchResult` and `SaveProfile`. **Result:
     the match is played and nothing is granted or saved.**
   * If the *second* `GetRoomInfo` (`:379`) fails instead, `Room.MemberCount` throws, `CloseRoom` aborts with no
     bots spawned, and the same match-end crash follows.
   * The Lobby login needs `auth/encrypt_token` + `auth/verify` to work for the GS's own super session, not only
     for clients.
2. **lobbyconfigs for `gs`.**
   * `InitWorld` sets `ServerType = room.ServerType` (`EN/ServerComponent.cs:589-593`), and `room.ServerType`
     comes from `game.server_type` (`LA/GameConfig.cs:19`, default `"default"`). If it is not `"gs"`, `IsGS` is
     false (`GM/ServerComponentExtension.cs:12-15`): `GameMode` is not set to PVP, and the `IsGS` branch of
     `InitWorld` is skipped (`GM/GameServerComponent.cs:1306-1309`).
   * The Controller swaps `GameApplication.Args` for the gs `cmd_args` while the World is built
     (`LA/Controller.cs:71-98`). If those args carry `-Level` (for example a copy of the TS args that load
     `Levels/Menus.level`, see verify-C2), the GS loads that level instead of a PvP map
     (`GM/GameServerComponent.cs:1222-1225`).
3. **Fed-id consistency.**
   * Room members carry `fed_id = auth/verify.account` (`LA/BaseServer.cs:406-429`).
   * The GS profile is loaded through the per-user `SessionForUser` (`GM/GameServerComponent.cs:934-944`).
   * `ApplyMatchResult` looks up `profile.FedId` in the room-keyed dictionary (`GM/GameplayComponent.cs:991`).
     If the stub returns different fed ids on the two paths, **trophies are silently skipped**.
   * `RemoveExtraHumans` (`GM/GameServerComponent.cs:485-494`) kicks connected players whose fed id is not a
     room member.
4. **Daily currency caps.** `CreditsCurrencyMatchRewards.DailyCap` is 80–160 and `VersusCoinsMatchRewards.DailyCap`
   is 1000–3000, by division (data pack). After the cap, Credits and VersusCoins drop to 0 per match. Trophies, XP
   and stats keep accruing. The counters reset in `CheckTime` (`GM/MetagameComponent.cs:1155-1164`).

## 5. Anti-farming check

I searched for `SpawnedAsAI`, `IsAI`, bot and human counts, and AFK in the reward, achievement, profile and league
code. **No reward suppression exists for bot-filled or low-human matches.**
* `IsPrivateMatch` zeroes PvP currency, MMR and stats, but it is true only when the room has the attribute
  `privateMatch` (`GM/GameServerComponent.cs:110-117`). That attribute is set only on the private-match create and
  find paths (`GM/GameClientComponent.cs:1530, 1945, 1951, 2063`), never by quick-match automatch.
* AFK kicking is off unless CRM `_ServerConfig…AFK.AfkActivated` is true (`GM/GameMatch.cs:409-424`,
  `GM/CharacterInputComponent.cs:337`). Even then it kicks the player rather than zeroing rewards.
* The `_humans` / `_bots` room attributes are written for stats only (`GM/GameServerComponent.cs:371-373`).
* There is no minimum number of humans anywhere except `MinPlayers` itself.

## 6. Is an IL patch a better route?

The patch is not needed for the GS route. These optional one-site patches would help:
* **Route the BOTMATCH tile to the GS flow.** In `Game.dll`, method
  `Game.GameClientComponent::'<NativeStartMatchWithBots>b__354_0'`, change `IL_0007: ldc.i4.2`
  (`OfflineMatchWithBots`) to `ldc.i4.1` (`OnlineAndTutorial`). This is one byte, `0x18` → `0x17`. After the patch
  the tile behaves like "Battle": onboarding before activation, bot-filled PvP on the in-process GS afterwards.
* **Hardening.** Make `GameplayComponent.ApplyMatchResult` skip the call when `Room` is null, or catch around
  `GetPendingLosses`. This removes the §4.1 "match played, nothing saved" failure. The alternative is to make sure
  the Lobby never fails.
* If MMR should move in solo play, `GetPendingLosses` would have to add bots as opponents. I do not recommend it;
  the official servers did not do this either.

The no-GS alternative (patching `OfflineBotsGameplayComponent` to drop the `PVPActivated` gates) can at most add
career trophies, MVP and merc XP. It still gives no division trophies, stats or PvP currency formula, so the GS
route is strictly better for progression.

## 7. Corrected claim

After PvP activation, free bot matches pay only `BotMatchesInfo` row 1 (stock: 5 Credits per win, 0 per loss).
They give no trophies, MVP or merc XP, although offline achievement trackers and per-merc counts still advance.

GS quick matches with one human are always topped up to 8 by `CloseRoom`, which fills 7 bots through
`SpawnBot` and gives 4v4. With stock template values this happens after about 50 s. With CRM
`configs/users/me/game` → `_ServerConfig.default.Matchmaking = {MinPlayers:1, MaxOpenTimeInSeconds:0}` it happens
immediately.

The human then receives:
* division-capped currency;
* MVP and merc mastery XP;
* career and division trophies, so league progress;
* stats, achievements and a leaderboard post.

MMR does **not** change, because bots are never room members.

This needs no code change, provided that:
* the profile is PvP-activated;
* the stub keeps the in-process GS's Lobby round-trips working (otherwise rewards can be silently lost);
* lobbyconfigs `gs` returns `server_type: "gs"` and `cmd_args` without `-Level`;
* CRM `configs/users/me/game` returns 200;
* the stub returns consistent fed ids.
