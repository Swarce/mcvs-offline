# 07 – Existing offline modes and bot matches

Scope: everything MCVS v1.17.32 already ships for playing without other humans. That covers the tutorial,
the onboarding bot matches, the "bot match" menu entry, the practice range, and the bots that fill online
matches. For each one: how it starts, what it simulates where, which maps/modes/bot counts/difficulties it
uses, what it pays out, what server pieces it needs, and what to change so that bot matches become a
first-class offline mode with full progression.

Paths are relative to `/home/user/mcvs-work`. Abbreviations:

* `GM/` = `decompiled/Game/Game/`
* `EN/` = `decompiled/Gameloft.Engine/Gameloft.Engine/`

Tags: **[V]** = verified (I read the code or IL). **[L]** = likely (a strong inference; not every link was
checked, or it depends on game data we do not have). **[S]** = speculative.

Companion reports: 01 (argument injection), 02 (Federation stub), 03 (profile storage and save timing),
04 (economy/CRM tables), 05 (netcode, TS/GS), 06 (LocalAnubis embedded server and P2P).

---

## 0. TL;DR

1. **Every existing offline mode is an "offline link", and the client World simulates it.** The client creates
   an ephemeral `ClientLink` with `ServerLocation.None` and `AnubisCommand.None`. It pauses the Transaction
   Server (TS) link and loads a level locally. Once that link is current, `Synchronization.IsServerSlave == false`.
   That makes the client World the authority (`Authority.IsMaster`): AI, scoring and match flow all run in the
   client World. **No `GameServerComponent` simulates these matches.** In-process or remote, the TS only
   (a) chooses the match parameters and pre-rolls the rewards, and (b) applies the client-reported result.
   **[V]** (`GM/GameClientComponent.cs:3736-3756`, `EN/ClientComponent.cs:1095-1102`, `EN/ClientLink.cs:124`,
   `EN/SynchronizationComponent.cs:458-467`, `GM/Authority.cs:7-15`)
2. **Four offline links exist:**

   | Link id | Started by | Level | Purpose |
   |---|---|---|---|
   | `OfflineLevel_Pending` | the TS handshake says `FinishedTutorial=false` | level index 1 = `Levels/Port/Port_Tutorial.level` | scripted tutorial |
   | `TrainingMatchWithBots` | UI `StartAutoMatch` (during onboarding) or `StartMatchWithBots` | a map picked by the TS | 4v4 bot match |
   | `PracticeLevel_<mercId>` | UI `Practice(mercId)` | `Levels/Gym/Gym_PVP.level` | practice range / training ground |
   | `OfflineLevel_Connected` | UI `LoadOfflineLevel(path)` | any level path | generic sandbox |

   **[V]** (`GM/GameClientComponent.cs:141-143, 3680-3692, 3968-3971, 4041-4058`, `GM/MercsComponent.cs:225, 391, 2347-2361`;
   the level list comes from the captured `MCVS.game`, see §9.3)
3. **Starting a bot match always needs a connected, unpaused TS link.** The client calls the RPC
   `OfflineBotsGameplayComponent.RequestOfflineBotsMatchRemote` on the TS. The TS answers with
   `MatchStarted`, `IsOnboardingMatch` and `OfflineBotsParameters` (map, match type, squad, and so on). Without a
   TS, `StartAutoMatchAsync` returns at once and does nothing. **There is no fallback to bots when the TS or GS
   cannot be reached.** **[V]** (`GM/GameClientComponent.cs:3973-3995`, `GM/OfflineBotsGameplayComponent.cs:208-269`)
4. **Two different "bot match" flavours share this code:**
   * **Onboarding** (`profile.PVPActivated == false`): the normal "Battle" button (`StartAutoMatch`,
     `MatchConnectionType.OnlineAndTutorial`) is silently turned into a scripted bot match. Each match comes from
     the AI-DB tables `TutoMatches` and `Match<N>`. After every match the player gets career trophies; once the
     league division is no longer `IsFTUE`, the TS calls `ActivatePVP`. From then on "Battle" goes to real
     matchmaking (GS).
   * **Bot match** (`MatchConnectionType.OfflineMatchWithBots`, UI `StartMatchWithBots`, menu item type
     `BOTMATCH`): always allowed. The map is random among maps flagged `BotMatch`, the match type is drawn by
     probability, the bots are 3 allies plus 4 enemies, and the difficulty ramps with that day's bot wins.
     `BotMatchesAvailable` is never set to `false` anywhere, so the menu entry is never locked by code.
   **[V]** (`GM/OfflineBotsGameplayComponent.cs:271-298, 317-354`, `GM/MetagameComponent.cs:1155-1164`, `GM/MainMenuComponent.cs:762-769`)
5. **Rewards: bot matches after PvP activation are deliberately second-class.** The TS pays the
   `BotMatchesInfo` reward tables only. There are **no trophies, no division trophies, no MVP bonus, no merc
   mastery XP, no MMR, no stats (kills/wins/...)**, and no TLE achievement progress (`GameMode.BotMatch` is
   excluded). Onboarding matches do pay career trophies, MVP and merc XP. Full progression exists only on the
   GS PvP path (`GameplayComponent.OnMatchOverAsync`). **[V]** (§5)
6. **Online matches already fill with bots**, server-side on the GS. `EnableBotInLobby` (default `true`, CRM
   `Matchmaking.EnableBotInLobby`) adds bots during the lobby, and `CloseRoom` tops the room up to `MaxPlayers = 8`.
   Bots are cloned from a human's profile, and their difficulty comes from MMR (`PVPBots` table). When a human
   disconnects, a bot takes over their player. **[V]** (`GM/GameMatchmakingComponent.cs:327-361, 751-805`,
   `GM/GameServerComponent.cs:345-455`, `GM/MultiplayerComponent.cs:1496-1543`)
7. **Recommended plan for full-progression bot matches:**
   * Use the **embedded GS** path (report 06): `ServerLocation.Embedded` plus LocalAnubis plus the Federation
     stub. A one-human "PvP" room fills with bots and pays **full** PvP rewards. This needs **no game-code
     change**, only CRM `Matchmaking` tuning (`MinPlayers: 1`, `MaxOpenTimeInSeconds: 0`), and it is also the
     path that later lets other humans replace bots (LAN/P2P).
   * As a no-GS alternative, apply a **5-site IL patch** to `OfflineBotsGameplayComponent` (§12.3) that removes
     the `PVPActivated` gating on trophies, MVP and merc XP.
   * In both cases, patch `GameClientComponent.get_ShouldPlayTutorial` if the tutorial should still play under
     Embedded. As shipped, the Embedded mode skips it (§6).

---

## 1. How an offline link works (engine level) [V]

```csharp
// GM/GameClientComponent.cs:3736-3756
public async Task LoadOfflineLevel(string linkName, LevelArguments level, CancellationToken ct) {
    IsInPractice = linkName.Contains("PracticeLevel");
    ui?.DispatchEvent("HIDE_LOADING", null);
    var clientLink = new ClientLink(World.Client, linkName);
    clientLink.Settings.AnubisCommand   = AnubisCommand.None;
    clientLink.Settings.ServerLocation  = ServerLocation.None;   // => ClientLink.Offline == true
    clientLink.Settings.LoadFromCurrentLevel = false;
    clientLink.Settings.InitialLevel    = level.TemplateFile;
    ConnectLink(clientLink);          // adds as Ephemeral link
    await Connect(clientLink, ct);    // see below
    await World.LoadLevel(level, ct); // the client loads the level itself
}
```

`ClientComponent.Connect(link)` (`EN/ClientComponent.cs:754-783`) runs `Prepare`, then `GetRoom`, then
`ConnectToServer`:

* `Prepare` calls `Authentication.Connect` (`EN/ClientComponent.cs:987-994`). That is `OpenAnonymousSession`, which
  is a no-op if a session is already open (`EN/AuthenticationComponent.cs:429-484`). **An open Federation session
  is therefore required even for offline links**, although in practice the session already exists by the time
  the menu is up.
* `GetRoom` with `AnubisCommand.None` sets `Room = null` (`EN/ClientComponent.cs:1019-1023`).
* `ConnectToServer` (`EN/ClientComponent.cs:1095-1102`): `if (link.Offline && PauseCurrentLinkUponConnection) {
  await PauseCurrentLink(link); CurrentClientLink = link; return; }`. This sends `Pause` on the TS link.
  `GameClientComponent.PauseServerInternal` also pauses client-side synchronization (`GM/GameClientComponent.cs:2942-2970`).
  On the TS, `DoPauseServer` saves the profile, releases it (`ProfileOwnerRoomId = null`), calls `DisableSave()`
  and sets `AutoSave = false` (`GM/GameServerComponent.cs:756-765`).

Who is authoritative while the offline link is current:

* `SynchronizationComponent.IsServerSlave` is `IsClient && !Client.Offline`, so it is **false**
  (`EN/SynchronizationComponent.cs:458-467`).
* `Authority.IsMaster(world)` is `!IsServer ? !IsServerSlave : true`, so it is **true** in the client World
  (`GM/Authority.cs:7-15`).
* `MultiplayerComponent.IsOffline` is true (`GM/MultiplayerComponent.cs:298-316`). `ForceOffline` is only a
  template property; no code sets it (`GM/MultiplayerComponent.cs:232`).
* `[Remote]` RPCs run **locally** in the client World. `RemoteUtil.ShouldInvoke` returns false when the
  client's `CurrentAnubisCommand == None`, or when the callee permission equals the local role
  (`EN/RemoteUtil.cs:23-39`). So in-match RPCs such as `RequestSpawnCharacterRemote` execute in the client World.
* The client-world `GameServerComponent` exists, but it is disabled. `World.Server` is only used for its
  `IsPrivateMatch` flag and connection events. **Nothing runs a GS for offline links.**

Leaving: `GameplayComponent._ExitGameplay` (`GM/GameplayComponent.cs:1326-1386`) closes the ephemeral link,
reconnects or resumes the TS link (`ResumeServer`, with `{"IsPractice": true}` when the level was the practice level),
and restarts synchronization. On resume, the TS **reloads the profile from storage** (`ReloadProfileAsync`) and
saves it again (`GM/GameServerComponent.cs:735-754`). The tutorial link (`OfflineLevel_Pending`) instead goes
through `FinishTutorialAndSynchronize` (`GM/GameplayComponent.cs:1260-1279, 1339-1345`).

---

## 2. Entry points (UI native functions) [V]

The HTML/JS menu (in the game data pack, which we do not have) calls named native functions. The C# side
registers them as follows:

| Native function | Handler | What it does | Evidence |
|---|---|---|---|
| `StartAutoMatch` | `GameClientComponent.StartAutoMatch()` | `StartAutoMatchAsync(OnlineAndTutorial)`: the TS decides between bots and the GS | `GM/GameClientComponent.cs:3791, 3953-3956` |
| `StartMatchWithBots` | `NativeStartMatchWithBots()` | `StartAutoMatchAsync(OfflineMatchWithBots)`: always bots | `GM/GameClientComponent.cs:3812, 3963-3966` |
| `EnterTeamRoom` | `NativeEnterGroupPlayTeamRoom()` | during onboarding: `StartAutoMatch()`; afterwards: group-play team room | `GM/GameClientComponent.cs:1248-1258, 3806` |
| `LoadOfflineLevel(path)` | `NativeLoadOfflineLevel` | `LoadOfflineLevel("OfflineLevel_Connected", path)` | `GM/GameClientComponent.cs:3811, 3968-3971` |
| `UnloadOfflineLevel` | `UnloadOfflineLevel()` | disconnects the current link if it is offline | `GM/GameClientComponent.cs:3723-3729, 3813` |
| `Practice(mercId)` | `MercsComponent.NativePractive` | `LoadOfflineLevel("PracticeLevel_<id>", "Levels/Gym/Gym_PVP.level")`, then `DefaultMercDeck[0] = id` | `GM/MercsComponent.cs:225, 391, 2347-2361` |
| `GetMatchTypeInfo` | `MainMenuComponent.NativeGetMatchTypeInfo` | menu match tiles from table `MenuMatchInfos`. For `Type == "BOTMATCH"`, `isLocked = !profile.BotMatchesAvailable` plus a timer | `GM/MainMenuComponent.cs:73, 134, 702-770` |
| `GetExitPermit` | `GameplayComponent.GetExitPermit` | true for ShootingRange, and for a bot match only when `MatchConnectionType == OfflineMatchWithBots` | `GM/GameplayComponent.cs:1410-1422` |

Also: `StartAutoMatchSameAsPlayerLevel()` (public, same as `StartMatchWithBots`) is only called by the cheat
`PlayAgainstBots` (`GM/CheatsComponent.cs:693, 2443-2450`).

**[L]** The "Battle" button calls `StartAutoMatch`. The `BOTMATCH` tile (when `MenuMatchInfos` marks it
`Displayed`) calls `StartMatchWithBots`. The UI scripts are not in the APK, so this wiring is inferred from the
names.

### 2.1 Start sequence (bot match) [V]

```
UI StartMatchWithBots / StartAutoMatch
 └─ GameClientComponent.StartAutoMatchAsync(ct, connType)                  GM/GameClientComponent.cs:3973
     ├─ return if !IsTransactionServerLink(CurrentClientLink) || TS connection null/Paused
     ├─ RPC → TS: OfflineBotsGameplayComponent.RequestOfflineBotsMatchRemote(connType)
     │     TS: PrepareOfflineMatchOnServer(profile, connType)             GM/OfflineBotsGameplayComponent.cs:243
     │       ├─ MatchConnectionType = connType
     │       ├─ if !ShouldPlayAgainstBots(profile) → {MatchStarted:false}
     │       ├─ pre-roll winRewards / loseOrDrawRewards (RewardComponent.Win/Lose, isOfflineMatch:true,
     │       │   isInTraining: connType==OnlineAndTutorial) → stored in profile.OfflineMatch*Rewards
     │       └─ return {MatchStarted:true, IsOnboardingMatch: !PVPActivated, MatchParameters}
     ├─ if MatchStarted:
     │     StartTrainingMatchWithBots(ForcedOfflineMap ?? params.LevelArgs, connType,
     │                                ForcedOfflineMatchType ?? params.MatchType, isOnboarding)
     │       ├─ LoadOfflineLevel("TrainingMatchWithBots", level)   (pauses TS link)
     │       ├─ gameplay.NbOfflineBotsToSpawn = 7
     │       ├─ onboarding: BotConfiguration = TutorialFirstMatches
     │       │  else:       BotConfiguration = HumanPlayerHighestLevel; BotDifficulty = SoloBots[dailyWins+1]
     │       ├─ gameplay.GameMode = Onboarding | BotMatch
     │       └─ gameplay.CurrentMatchType = matchType
     └─ else: ConnectGameplayServer(QuickMatchServerLink) → online matchmaking (GS)
```

`ShouldPlayAgainstBots` (`GM/OfflineBotsGameplayComponent.cs:271-283`):

```csharp
if (MatchConnectionType == OfflineMatchWithBots) return true;
if ((World.Server as GameServerComponent)?.IsPrivateMatch == true) return false;
return !profile.PVPActivated;
```

`enum GameClientComponent.MatchConnectionType { Undefined = 0, OnlineAndTutorial = 1, OfflineMatchWithBots = 2 }`
(`GM/GameClientComponent.cs:30-35`).

**No connection-failure fallback.** `OnConnectionError` and `OnOnlineError` only retry or show an error popup
(`GM/GameClientComponent.cs:3322-3350, 3370+`). `Authentication_OpenSessionFailed` shows a popup and calls
`Connect` again (`GM/GameClientComponent.cs:3938-3951`). If the GS automatch fails, nothing falls back to bots. **[V]**

---

## 3. Match parameters chosen by the TS [V]

`OfflineBotsParameters` (`GM/OfflineBotsGameplayComponent.cs:18-88`) is serialized in the RPC reply. Fields:
`MatchNumber, LevelArgs, NbTrophies, LocalPlayerSquad, ElixirGainPerSecond(-1), TimeIntervalForPoints, MinPlayerHealth,
SelectedZoneId(-1), EnemiesFirstSpawnDelay, NbMatches, MatchType, MatchTimeLimit, MatchStartTimerWithDuplicateMercs,
RespawnTimerWithDuplicateMercs, SuperChargeEnabled`.

`ApplyTableProperties(profile)` (`GM/OfflineBotsGameplayComponent.cs:317-354`) works in two branches.

* **Onboarding (`!PVPActivated`)**: per-user AI DB table **`TutoMatches`**. Row `Match = clamp(AllTimeOfflineVictories+1,
  1, nbRowsWithMap)`. Columns read: `Map` (level path), `Trophies`, `TimeIntervalForPoints`, `ElixirGainPerSecond`,
  `PlayerHealthFailSafe` (becomes `HealthComponent.Min` of the local player, so the player cannot die below it,
  `GM/OfflineBotsGameplayComponent.cs:696-703`), `PlayerSquad`, `EnemiesFirstSpawnDelay`, `ZoneID` (forced
  ControlPoint zone, `GM/ControlPointMatchComponent.cs:362-375`), `MatchType` (enum name), `MatchTimeLimit` (Uphold only),
  `MatchStartTimerWithDuplicateMercs`, `CharacterSelectionScreenDurationWithDuplicateMercs`, `SuperChargeEnabled` (y/t/empty = true).
  The match advances only on **wins**, because `AllTimeOfflineVictories++` runs on wins only.
* **Post-onboarding**: `GameMatchmakingComponent.GetLevelToLoad(GameMode.BotMatch)` makes a weighted random pick among
  `Maps` rows whose `AvailableInGameModes` contains `BotMatch` (`GM/GameMatchmakingComponent.cs:893-915`,
  `GM/MapsComponent.cs:85-94`, `GM/Map.cs`). Then `GameplayComponent.SelectMatchTypeFromDatabaseProbability(mapPath, fedId)`
  reads the Gameplay DB table `MatchTypeProbability`: row `Index == map.Id`, one weight column per MatchType name
  (`GM/GameplayComponent.cs:656-705`). The squad is randomly `"Arc"` or `"Korp"`. `SuperChargeEnabled = true`.

The client applies these values when gameplay starts and when the match changes. That covers ZoneBased
`TimeIntervalForPoints`, `ElixirGainPerSecond`, Uphold `TimeLimit`, `SuperChargeEnabled`, and the character-select
timers (`GM/OfflineBotsGameplayComponent.cs:638-689`).

Client-side overrides are `GameClientComponent.ForcedOfflineMap` and `ForcedOfflineMatchType`. They are public
properties, set only by the LOCAL cheats `ForceOfflineMatchMap` and `ForceOfflineMatchType`
(`GM/GameClientComponent.cs:269-271, 3985`, `GM/CheatsComponent.cs:3187-3223`). On the TS,
`GameMatchmakingComponent.ForceLevelIndex` (cheat "ForceMapNumber") and `GameplayComponent.ForcedMatchType`
(cheats "ForceUphold"/"ForceControlPoint") also affect `GetLevelToLoad` and the match-type pick
(`GM/CheatsComponent.cs:3882-3910`).

---

## 4. The match itself (client World) [V]

### 4.1 Squads, spawning, bot counts

* Squad selector (`GM/MultiplayerComponent.cs:423-438`): with `OfflineLevel_*` links or ShootingRange it is
  `TutorialSquadSelector` (everyone on `"Korp"`). With a bot match in progress it is `OfflineBotsMatchSquadSelector`,
  where only the local player is assigned, to `LocalPlayerSquad` (`GM/OfflineBotsMatchSquadSelector.cs`).
* The local player is created by `AddPlayerConnection(Session.FedId)` with a null connection
  (`GM/MultiplayerComponent.cs:837-841`). Once the profile is initialized and the low-res character exists, the
  client calls `SpawnOfflineBots(NbOfflineBotsToSpawn=7, BotDifficulty, MyPlayer.Squad, BotConfiguration)`
  (`GM/MultiplayerComponent.cs:858-878`).
* `SpawnOfflineBots` (`GM/MultiplayerComponent.cs:555-573`) works as follows:
  * `TutorialFirstMatches` → `SpawnBotsFromExcelTutorialConfig`. The AI DB table **`Match<MatchNumber>`** has one
    row per bot, with columns `Name, MercID, Team (Ally|Enemy), Health, Weapon.DamageMin, Weapon.DamageMax,
    MinDistanceToTarget, MaxDistanceToTarget, ImprecisionRatio, AdditiveError, PlayerLowHealthAdditiveError, Behavior,
    TimeToRespawn, MinHealth, Difficulty, CanUseAbility, TargetStrategy, PathfindingCourage, ProtectPlayerIfInFewSeconds,
    StrafeCombatExperience, StrafeDelaySeconds`. Bots get merc level 1 and no profile. Enemies spawn after
    `EnemiesFirstSpawnDelay` (`GM/MultiplayerComponent.cs:631-704, 742-747, 524-553`). The bot count is whatever the
    table holds.
  * `HumanPlayerHighestLevel` → **3 allies + 4 enemies, hard-coded** (`hlp_spawnBotTeam(team, 3|4, ...)`,
    `GM/MultiplayerComponent.cs:583-598`). That makes 4v4 with the human. Every bot gets
    `merc level = max level among the player's displayed mercs`, `AiModPower = ModsMetaComponent.GetBestPowerValue(player)`,
    and difficulty `BotDifficulty`. `NbOfflineBotsToSpawn` is only checked for `> 0`.
  * One `Templates/AI/Commander.tx` (SquadComponent) is added per team (`GM/MultiplayerComponent.cs:711-730`).
* Bot identity: `CreateBotWithProfile` (`GM/MultiplayerComponent.cs:1197-1239`) builds a `PlayerProfileComponent`
  from `DefaultProfile.tx` (or from the human's template when one is given). The credential is `dev:<name>`, where
  the name is drawn from the AI DB table `BotNames` (column 0). Note: the method ignores the `botName` argument and
  draws a second name. The bot gets every merc unlocked (`CheatsComponent.UnlockAllMercs`), all signatures with
  random kill-signature cosmetics, MMR/GentleTrophies ±5 %, and `DisableSave()`. `InitBotPlayer` picks the merc
  with `AiUtils.GetRandomBotMerc`: AI DB table `AllowedBots` (`[id, enabled]`) ∩ unlocked/displayed, preferring
  mercs not yet in the team (`GM/AiUtils.cs:10-50`). It then enables `BT_Root` and applies difficulty
  (`GM/MultiplayerComponent.cs:1160-1186`).

### 4.2 Difficulty system [V]

* Post-onboarding bot matches: `botDifficultyFromDailyOfflineMatches()` reads AI DB table `SoloBots` (`Match` → `Difficulty`)
  at row `clamp(BotMatchesDoneInTheDay+1, 1, rows)`, clamped to the `[min,max]` of `BotDifficulty.Difficulty`. It
  returns -1 if a table is missing, and in that case `InitBotPlayer` falls back to `ApplyBotDifficultyFromTrophies(botMMR)`
  (`GM/GameClientComponent.cs:3997-4034`, `GM/MultiplayerComponent.cs:1178-1185`). `BotMatchesDoneInTheDay` increases
  only on post-onboarding **wins**, and resets at the daily reset (`CheckTime`, which needs `profile.IsTimeZoneSet`)
  (`GM/OfflineBotsGameplayComponent.cs:565-571`, `GM/MetagameComponent.cs:1155-1164`). **Bots therefore get harder
  with each win of the day.** There is no UI to choose a difficulty.
* `BlackBoardComponent.ApplyBotDifficulty(d)` reads AI DB table **`BotDifficulty`**, row `Difficulty == d`, with columns
  `ImprecisionRatio, AdditiveError, RotationSpeed, ZoomWhenShooting, HeadShotStrategy (NoHeadShot|Random|Adaptive),
  HeadShotRandom, CanUseAbility, StrafeReloadExperience, StrafeHealingExperience, TimeToRespawn, TargetStrategy
  (ClosestTarget|MostDangerous|Protect), StrafeDelay, ModTier`. `ModTier` sets `ForcedMercModTier` when
  `AiModPower == -1` (`GM/BlackBoardComponent.cs:1919-1975`).
* GS/online bots: `ApplyBotDifficultyFromTrophies(mmr)` reads AI DB table **`PVPBots`** (`MMR` → `Difficulty`)
  (`GM/BlackBoardComponent.cs:1877-1917`).
* Behaviour enums: `AiPositionBehavior {Undefined, Hunter, FollowHuman, CaptureZone, FirstCaptureZoneThenHunt}`,
  `StrafeExperienceEnum {CannotStrafe, StrafeAccordingToClosestTarget, StrafeAccordingToMainThreat}`
  (`GM/BlackBoardComponent.cs:1212-1219`, `GM/AwarenessComponent.cs:25-30, 61-66, 179-184`).
* `BotAttractor` (level component, `OnlyInTutorialMatches = true`, `Radius = 25`): during onboarding
  (`InTutorialPhase`) it restricts every player's combat area to a circle around itself (`GM/BotAttractor.cs:44-87`).
* During onboarding, `BT_Root` also stops bots from changing merc (`GM/BT_Root.cs:486-494`).

### 4.3 Other match rules in offline levels

* Merc cooldowns are ignored when offline (`GM/PlayerComponent.cs:1006`).
* In the practice level (`IsInPractice`), mercs that are not owned may be played (`GM/PlayerComponent.cs:988`).
* The HUD exit button is enabled (`ui.SetPropertyValue("GameExitPermit", isOffline)`, `GM/GameplayComponent.cs:362-366`).
  For bot matches, though, `GetExitPermit` only allows it for `OfflineMatchWithBots`.
* Medals are tracked because the client is master (`GM/PlayerMedalTrackerComponent.cs:38`).
* Time-limited (TLE) achievement HUD/progress is disabled for `GameMode.BotMatch` (`GM/TimeLimitedAchievementsComponent.cs:204, 223`).

### 4.4 Developer/test bots

* `AutoBotComponent` (`GM/AutoBotComponent.cs`) drives the **local player's** character by writing input. It sprints,
  zooms, uses abilities, and respawns as a random merc from a hard-coded id list `{1..16, 18, 19, 20}`. It is only
  added by `BotTestComponent` (`GM/BotTestComponent.cs:236-245`).
* `BotTestComponent` runs only with the launch arg `-BotTest` (options `-BotTestRematchCount`,
  `-BotTestMaxMatchTimeSeconds`, `-SkipGameplay`). It sets `ForceSkipTutorialAndAgeGate`, runs cheats
  (`UnlockAllMercs` when developer, then `SkipTraining`), starts **group play** (GS) matches, autoplays them, and
  finally **kills the process** (`GM/BotTestComponent.cs:56-150`). It is useless for players, but it shows that
  `SkipTraining` plus GS matches is the developers' own soak-test path.

---

## 5. End of match and rewards

### 5.1 Client side (`GameplayComponent.OnMatchOverAsync` → `OfflineBotsGameplayComponent.OnEndOfMatch`) [V]

`GM/GameplayComponent.cs:930-938` handles the non-server, non-slave case with a bot match in progress by calling
`OnEndOfMatch(matchResult, myResult)` (`GM/OfflineBotsGameplayComponent.cs:375-399`):

* `!PVPActivated`: client-side `ApplyTrophiesResult(giveDivisionTrophies:false)` (display only), MVP from
  `MatchTopPlayersManagerComponent`, `ProcessRewards(..., ScoreWithAgent, isMVP)` (display), then
  `SendMatchResult(agentScores = ScoreWithAgent, isMVP)`.
* `PVPActivated`: `ProcessRewards(result, {}, isMVP:false)` and `SendMatchResult(agentScores = {}, isMVP:false)`.
  **The client does not even send agent scores or MVP after onboarding.**

Quitting early (`ExitingGameplayEvent` while the match is not `Over`) sends a **Loss** with `isMatchFinished:false`
(`GM/OfflineBotsGameplayComponent.cs:365-373`). The TS still applies the lose rewards (and, during onboarding, the loss
trophies), but skips achievements and `OnMatchOver`.

### 5.2 Wire message (TS control message on the paused TS link) [V]

`SendMatchResult` loops until the TS link has a connection, then calls
`SendControlMessage(MessageType.Message, msg)` (`GM/OfflineBotsGameplayComponent.cs:401-454`):

```json
{
  "action": "OfflineMatchAgainstBotsOver",          // GameClientComponent.CustomRequestName = 14
  "messageType": "Message",                           // added by Connection.SendControlMessage (report 05)
  "matchResult": { "TeamScore": 300.0, "EnemyScore": 120.0, "Score": 1450,
                   "Result": 1,                        // MatchResultType {Loss=0, Win=1, Draw=2}
                   "MatchType": 3 },                   // MatchType enum as int (Newtonsoft default)
  "mercsUsed": [13, 4],
  "isMatchFinished": true,
  "agentScores": { "13": 900, "4": 550 },             // mercId -> score; {} after PvP activation
  "isMVP": true
}
```

(`GM/OfflineBotsGameplayComponent.cs:456-473`, `GM/OfflinePlayerMatchResult.cs`)

### 5.3 TS side (`OnControlMessageReceived`, `GM/OfflineBotsGameplayComponent.cs:529-636`) [V]

1. Parse the message and look up the profile by `connection.FedId`. Restore `MatchConnectionType`, `winRewards` and
   `loseOrDrawRewards` from the profile. **Abort if `OfflineMatchConnectionType == Undefined`.** That value is only
   set by `PrepareOfflineMatchOnServer`, so an unsolicited result is ignored.
2. On a win: `AllTimeOfflineVictories++`. If post-onboarding, also `BotMatchesDoneInTheDay++`. Decrement
   `FTUETapReminderQuickMatchCount`.
3. `MatchesPlayedUsingMerc[merc]++` for each merc used.
4. If `isMatchFinished`: run `BaseAchievementsComponent.OnMatchResultOffline(...)` (Wins, WinsInARow,
   WinWithEnemyScoreBelow30/50, PlayedMatches, MVP, score trackers; `GM/BaseAchievementsComponent.cs:635+`) and
   `MetagameComponent.OnMatchOver(profile, result, wasOnlineMatch:false)`, which sets `LastGameResult`.
5. `ProcessRewards(result, agentScores, isMVP, profile)` (`GM/OfflineBotsGameplayComponent.cs:475-517`):
   * pick the pre-rolled win or lose list;
   * add the MVP bonus **only if `!PVPActivated`**;
   * `Claim` each item;
   * add non-MVP `Credits` to `PVPCurrencyRewardCountForWins` (the shared PvP daily cap);
   * store `LatestEndMatchRewards`;
   * on the server and **only if `!PVPActivated`**: `AddMercXpForScore(merc, score, rounds)` for each agent score.
6. **If `!PVPActivated`**: `ApplyTrophiesResult(giveDivisionTrophies:false)` gives GentleTrophies only. Then, if the
   league division is not `IsFTUE`, call `ActivatePVP(profile)` and later `RefreshCrmConfig`. If the player has a
   name and no leaderboard id yet, `LeaderboardsComponent.PostScore(profile, 0)`.
7. Clear `OfflineMatch*` and save with `SaveProfile(forceSaveIfPaused:true)`. This only saves because
   `World.Server.IsPaused` is true at that moment (`EN/ProfileComponent.cs:63-79`).

### 5.4 Reward tables (`RewardComponent`, `GM/RewardComponent.cs:189-446`) [V]

| Call | `isInTraining` | `isOfflineMatch` | Result |
|---|---|---|---|
| Onboarding via "Battle" (`OnlineAndTutorial`) | **true** | true | Win: `GenerateOnboardingMatchRewards`, i.e. `AIOnboardingSpecs[Match-1 == AllTimeOfflineVictories].RewardsTable` ("Regular" rows). After the last spec it returns `null`, which means no reward. Lose: `[]`. |
| Bot match (`OfflineMatchWithBots`), any phase | false | true | `GenerateOfflineMatchRewards`: **first row only** of MetaDB `BotMatchesInfo` (`Match, HasCrate, WinRewardsTableName, LoseRewardsTableName`; `HasCrate` is unused). Each `DatabaseReward` gets `CreateRandomRewards`. `Credits` are capped at the division `CreditsCurrencyMatchRewards.DailyCap - PVPCurrencyRewardCountForWins`. |
| GS PvP (`GameplayComponent.ProcessRewards`) | false | false | `GenerateCurrency`: Credits and VersusCoins by division (`CreditsCurrencyMatchRewards` / `VersusCoinsMatchRewards`: `VictoryAmount, DefeatAmount, MVPAmount, DailyCap`), with Arena multipliers. Plus MVP. |
| Private match / tournament | – | – | inbox and Hermes messages only |
| `UIComponent.TutorialCompleted` (tutorial script) | true | false | onboarding rewards, but computed and claimed **on the client** for display only (`GM/UIComponent.cs:2128-2136`) |

### 5.5 What each path actually grants [V]

| Effect | Onboarding bot match | Bot match after PvP activation | GS PvP match (incl. bot-filled) |
|---|---|---|---|
| Currency/items | onboarding table (Battle) or BotMatchesInfo (bot tile) | BotMatchesInfo table | division Credits + VersusCoins (daily caps), MVP |
| MVP bonus | yes | **no** | yes |
| Merc mastery XP | yes (from `agentScores`) | **no** (client sends `{}`, server gated) | yes (`BuildAgentsMasteryRewards`, `GM/PlayerProfileComponent.cs:5609-5614, 5634-5680`) |
| Career trophies (GentleTrophies) | yes | **no** | yes |
| Division trophies / season | no | no | yes (+ `LeaderboardsComponent.PostScoreAsync`) |
| MMR (Glicko) | no | no | yes (`ApplyMatchResult`, `GM/GameplayComponent.cs:982-1052`) |
| Stats (Wins, Kills, Deaths, Headshots, MVPs, Eliminations, AllTimePVPMatches), session history, win/loss streak, per-map counts | no | no | yes (`GM/GameplayComponent.cs:855-883`, `GM/PlayerProfileComponent.cs` `OnProcessMatchResult`) |
| Achievements | offline variant | offline variant | full variant |
| TLE achievements | – | excluded (`GameMode.BotMatch`) | yes |
| `CratesTrackWinsCount` | no | no | yes (`GM/GameplayComponent.cs:1144`) |
| `AllTimeOfflineVictories` | +1 per win | +1 per win | – |
| PvP unlock (`ActivatePVP`) | when league is not FTUE | – | – |

Conclusion: **as shipped, post-tutorial bot matches cannot carry full progression.** That is a design choice
(PvE "practice" matches pay a small fixed table), not a missing server feature.

---

## 6. Tutorial and onboarding flow [V]

* Level list from `MCVS.game` (captured from the CDN, decoded in §9.3):
  `Levels: ["Levels/Connection.level", "Levels/Port/Port_Tutorial.level", "Levels/Menus.level"]`.
  `GameClientComponent.TutorialLevelIndex = 1` (`GM/GameClientComponent.cs:123`).
* The TS handshake reply contains `FinishedTutorial = playerProfile.FinishedTutorial` (`GM/GameServerComponent.cs:1065`).
  The client sets `ShouldPlayTutorial = !FinishedTutorial` (`GM/GameClientComponent.cs:3625`). `ForceSkipTutorialAndAgeGate`
  overrides it (`3640-3645`).
* The `ShouldPlayTutorial` getter (`GM/GameClientComponent.cs:363-383`) returns true only if the current level index
  is 0 or 1, **and** `CurrentAnubisCommand == QuickLaunch`, **and** `CurrentServerLocation != Embedded`.
  **So with the embedded TS, the Port tutorial never plays.**
* When the TS link connects (`_GameClientComponent_Connected`, `GM/GameClientComponent.cs:3666-3693`): if the
  tutorial is pending, the client runs `LoadOfflineLevel("OfflineLevel_Pending", LevelArguments.FromIndex(1))`.
  Otherwise it calls `StartSynchronization()`, and the TS pushes the menu.
* The tutorial level runs `TutorialComponent : GameMatch` (`MatchType.Tutorial`), which provides scripted input
  filters, auto-shoot and AI-control toggles (`GM/TutorialComponent.cs`), driven by Gameloft.Sequences scripts in
  the level. It ends with `UIComponent.TutorialCompleted()`, which shows a client-side result with onboarding
  rewards, and then exit. `FinishTutorialAndSynchronize` unloads the pending link, resumes the TS and starts
  synchronization (`GM/GameplayComponent.cs:1260-1279`). On the TS, `FinishedTutorial = true` is set when server
  synchronization starts (`GM/PlayerProfileComponent.cs:2513-2518, 2906-2910`).
* **Onboarding = PvP not activated.** `InTutorialPhase(profile) == !profile.PVPActivated`
  (`GM/OfflineBotsGameplayComponent.cs:291-298`). The player gets scripted `TutoMatches` bot matches through the
  regular "Battle" button until enough GentleTrophies move them out of an `IsFTUE` league division. The TS then
  calls `ActivatePVP` (`GM/OfflineBotsGameplayComponent.cs:603-621`, `GM/MetagameComponent.cs:896-901`).
  FTUE scripts can test `aimatches == N` and `pvpactivated` (`GM/MetagameComponent.cs:654-667`), and FTUE reward
  steps gate on `TutoMatchesDone <= AllTimeOfflineVictories` (`GM/MetagameComponent.cs:864-884`).
* Cheat `SkipTraining` sets `AllTimeOfflineVictories = 999` and calls `ActivatePVP` (`GM/CheatsComponent.cs:2641-2645`).
* Profile migration: a legacy profile without a `PVPActivated` node gets `AllTimeOfflineVictories = 4` and
  `PVPActivated = true`. Old players are treated as past onboarding (`GM/ProfileManagerComponent.cs:657-670`). **[V]**

---

## 7. Practice range ("Training ground") [V]

* `Practice(mercId)` → `LoadOfflineLevel("PracticeLevel_<id>", "Levels/Gym/Gym_PVP.level")`, then `DefaultMercDeck[0] = id`
  (`GM/MercsComponent.cs:2347-2361`). It is tracked by `TrackTrainingGround(mercId)`.
* `IsInPractice = true` lets any merc be played, including locked ones (`GM/PlayerComponent.cs:988`). This works as
  a "try before you buy" mode.
* The match type is `ShootingRange` (`ShootingRangeMatchComponent`, an empty `GameMatch`). It uses
  `TutorialSquadSelector`. `GetExitPermit` returns true.
* There are no rewards. On exit the client resumes the TS with `{"IsPractice": true}`, which suppresses the
  `ResumeNoPractice` event (`GM/GameplayComponent.cs:1360-1367`, `GM/GameServerComponent.cs:745-750`).
* The developer cheat "spawn bot in training room" toggles `BT_Root` of existing AI players and cycles merc ids
  (`GM/CheatsComponent.cs:3377-3392`). So the gym level can contain AI dummies. **[L]**: placed by the level data.

---

## 8. Bots in online (GS) matches [V]

All of this runs in a headless GS World. With Embedded mode it is created in-process by `ServerManager._AddClientAsync`
(`EN/ServerManager.cs:146-184`; report 06).

* **Lobby fill.** `GameMatchmakingComponent.OnConstantUpdate` (`GM/GameMatchmakingComponent.cs:327-361`) starts once
  the first `GetLobbyInfo` sets `lobbyStartTime` (`705-719`). It computes
  `expected = min(floor(elapsed / MaxLobbyTime * MinPlayers), MaxPlayers)` (or, for group play,
  team-room arithmetic with `MaxPlayers` once `MaxLobbyTime` is reached), then calls `FillWithBots(expected)`, which
  spawns bots only while `EnableBotInLobby` is set and at least one active player exists (`795-805`).
* **Start.** `HasEnoughPlayerToStartMatch` = `ActivePlayersCount >= MinPlayers` (or private match) (`200-210`), polled every
  frame by `CheckGameplayInitialized` (`GM/GameplayComponent.cs:460-476`). It leads to `OnHasEnoughPlayerToStartMatchHandler`,
  then `CloseRoomAfterDelay(MaxOpenTimeInSeconds)` (`GM/GameServerComponent.cs:295-310`).
* **Close room** (`GM/GameServerComponent.cs:345-430`): updates the room (`_closed`, `_bots`, `_spawnedAsBots`, `_humans`,
  score stats), reserves `"bot"` in every free slot, trims extra bots and extra humans, then `UpdateBotsWithRoomInfo`.
  Room members who never connected get a bot that **wears their identity** (`ReplaceBotWithRoomUser`: name, avatar,
  `_score` → MMR, `_gentleTrophies`). It then tops up to `MaxPlayers` with `SpawnBot()` and `BalanceTeams()`.
  Without a Room it simply calls `FillWithBots(MaxPlayers)`.
* **`SpawnBot()`** (`GM/GameMatchmakingComponent.cs:751-758`) calls `CreateBotWithProfile(randomConnectedHuman, difficulty:-1,
  mercLevel:-1, modPower = human best)`. The bot clones the human's profile template, its difficulty comes from its
  ±5 % MMR through `PVPBots`, and its merc level comes from the cloned profile.
* **Terminator bots.** If one team is all bots and the other team's average MMR is at least `TerminatorThreshold` (2400),
  every bot on the all-bot team gets the difficulty for `TerminatorMMR` (20000) (`GM/GameServerComponent.cs:432-455`).
* **Disconnect takeover.** `RemovePlayerConnection(replaceByBot:true)` during a running match re-enables `BT_Root` on
  the leaver's player, with MMR-based difficulty (`GM/MultiplayerComponent.cs:1496-1543`). **Join takeover:** when the
  room is full, a joining human replaces a `SpawnedAsAI` player (`GM/MultiplayerComponent.cs:1353-1373`). Both
  matter for P2P: humans can drop into and out of bot-filled games.
* **Max lobby time** = `NoobSettings` entry `MaxLobbyTimeInSeconds` (the entry is chosen by cumulative `MatchCount`
  against `AllTimePVPMatches`), else the MetaDB table `MatchmakingBotAddingTime` (`TrophyRange` → `MaxLobbyTimeInSeconds`),
  else `DefaultLobbyTime = 1 min` (`GM/GameMatchmakingComponent.cs:95-113, 634-656, 1105-1121`).
* **CRM overrides** (GS only, `OverrideFromCRM`, `GM/GameMatchmakingComponent.cs:845-891`). Game parameter
  object `"Matchmaking"` with keys: `MaxOpenTimeInSeconds, MinPlayers, DynamicRangeInterpolate, DynamicRangeEnabled,
  MinDynamicRange, MaxDynamicRange, MinDynamicTimeInSeconds, MaxDynamicTimeInSeconds, DynamicRangeStep, ScoreBucketSize,
  UpdateLobbyRecurringDelayInMilliseconds, MatchmakingDistributionWindowInMinutes, TeamReviewDurationInMilliseconds,
  DisplayedExpectedLobbyTimeFactor, MaxDisplayedExpectedLobbyTimeInSeconds, EnableBotInLobby, EndedMatchExpirationInSeconds,
  GroupSynergyScoreMultipliers[], GroupSynergyScoreOffsets[], EnableBotFactor, ExpectedLobbyTimeMultiplier, MMRFilters[],
  TerminatorThreshold, MMRCap, NoobSettings[{Id, MatchCount, AffectMMR, MaxLobbyTimeInSeconds}], SplitGroupPlay`.
  A separate object `"MatchmakingRating"` holds `{MinDeviation, MaxDeviation, MaxDiffRating}`.
  `EnableBotFactor` scales MMR deltas by the number of opponents (`GM/GameplayComponent.cs:1039-1042`).
* Network side effects on the GS lobby (`UpdateMatchmakingDistribution`, `AddScoreEntry`) are wrapped in try/catch, so
  they cannot block bot fill (`GM/GameMatchmakingComponent.cs:1190-1345`). `AntiCheatManager.StopSession` is a no-op
  (`EN/AntiCheatManagerComponent.cs:35-37`).

---

## 9. Modes, maps and levels

### 9.1 Enums [V]

```csharp
enum MatchType   { None=0, TeamDeathMatch=1, Survivor=2, ControlPoint=3, ShootingRange=4, Uphold=5, Tutorial=6, Dominion=7 } // GM/MatchType.cs
enum GameMode    { None=0, PVP=1, Tournament=2, Onboarding=3, BotMatch=4, MENU=5, Arena=6 }                                  // GM/GameMode.cs
enum MatchResultType { Loss=0, Win=1, Draw=2 }                                                                               // GM/MatchResultType.cs
enum GameplayComponent.BotSpawningType { TutorialFirstMatches=0, HumanPlayerHighestLevel=1 }                                 // GM/GameplayComponent.cs:38-42
```

### 9.2 Match components (class hierarchy) [V]

| MatchType | Component | Base | Tracking name (`GetGameMode`) | Mode-specific medals |
|---|---|---|---|---|
| ControlPoint | `ControlPointMatchComponent` | ZoneBased → TeamBased → MultiplayerGameMatch | `RANKED_PVP` / `CONTROL_POINT_ARENA` | `Medals.ControlPoint.UnderPressure`, ZoneBased: `CaptureAfterKill, Defender, OnPoint, StayInZone` |
| Dominion | `DominionMatchComponent` | ZoneBased | `WAR_ZONE_PVP` / `WAR_ZONE_ARENA` | `CaptureZonesWithoutDying, Conqueror, Rebellion, TheB` + ZoneBased |
| TeamDeathMatch | `TeamDeathMatchComponent` | TeamBased | `DEATHMATCH` / `DEATHMATCH_ARENA` | general medals |
| Uphold | `UpholdMatchComponent` | TeamDeathMatch | `KILL_CONFIRMED_PVP` / `KILL_CONFIRMED_ARENA` (achievement tracker name "Bounty") | `CherryOnTop, FastRetrieve, FoundIt, RetrievalsWithoutDying` |
| Survivor | `SurvivalGameMatch` | GameMatch | – | – |
| ShootingRange | `ShootingRangeMatchComponent` | GameMatch | – | – |
| Tutorial | `TutorialComponent` | GameMatch | `TUTORIAL_PVE` | – |

General medals: `AbilityKill, ComeBack, DamageAbsorption, DeathDefying, FastKill, FirstBlood, HeadshotsWithoutDying,
KillsWithoutDying, SpreeEnder, TeamEradication, Vengeance, Vindicator` (`decompiled/Game/Game.Medals*`).
Tracking: `GameTrackingComponent.GetGameMode(isPrivate, isOffline, isTutorial)` returns `PVE` / `TUTORIAL_PVE` for offline,
`TOURNAMENT_PVP` for private matches, otherwise the table above (`GM/GameTrackingComponent.cs:18391-18440`). It is analytics only.
A level supports a match type when it contains `GameModes/<MatchType>` (`GM/GameUtil.cs:94-130`). `MatchTypeTagComponent`
objects for other types are disabled at start (`GM/GameplayComponent.cs:391-397`).
`TeamBasedMatchComponent.ScoreLimit` defaults to 300 (`GM/TeamBasedMatchComponent.cs:76`).
**[L]** `Survivor` looks like a dead or legacy mode, since nothing selects it in code. It is only reachable if data
lists it in `MatchTypeProbability`.

### 9.3 Levels and maps

* **[V] Game definition** (`captures/cdn/10_astc_entry_MCVS.game.zip.bin` → `MCVS.game`, a `GCBF` container whose body is a
  raw LZ4 block). Decoded with a 30-line LZ4 block decoder: `Levels = [Connection, Port/Port_Tutorial, Menus]`,
  `FramerateLimit 30`, `GameProfileSelector "_mcvs"`. Only these three levels are "indexed". PvP and bot maps are loaded by path.
* **[V] Only two map paths appear in code:** `Levels/Gym/Gym_PVP.level` (practice) and `Levels/New.level` (editor).
  **The bot/PvP map list is purely data**: MetaDB table read by `MapsComponent` (`PropertyTableComponent.Table`, columns
  `Index, Image_PrivateMatch, MapPath, NameTag, DescriptionTag, Image_Loading, MatchFilterOrder, MatchCreateOrder,
  DeveloperAvailable ("YES"), AvailableInGameModes ("PVP,BotMatch,Tournament,..."), SelectionProbability`) plus the
  Gameplay DB table `MatchTypeProbability` (`Index` + one weight column per MatchType) and `MatchTypeSpecs`
  (`Index, LocId, Img, Active("YES"), MatchFilterOrder, MatchCreateOrder`). `DeveloperAvailable` maps are added only for
  `IsValidDeveloper` profiles, and not on headless servers (`GM/MapsComponent.cs:45-94`).
* **[S]** The tutorial level path suggests a "Port" PvP map exists. The real list has to be read from the game data pack
  (report 10 describes the `.amz` download). Extracting `MetaDatabase`, `GameplayDatabase` and `AIDatabase` (their paths
  come from `DatabasePathsComponent`, which is data-set too) is a **prerequisite for listing maps, bot counts in
  tutorial matches, and the reward tables.**

---

## 10. Data tables the offline/bot features read

| DB (path from `DatabasePathsComponent`) | Table | Used for | Evidence |
|---|---|---|---|
| AI | `TutoMatches` | onboarding match parameters | `GM/OfflineBotsGameplayComponent.cs:124, 317-347` |
| AI | `Match<N>` | scripted bots per onboarding match | `GM/MultiplayerComponent.cs:631-704` |
| AI | `BotDifficulty` | AI tuning per difficulty | `GM/BlackBoardComponent.cs:1919-1975` |
| AI | `SoloBots` | bot-match difficulty by daily wins | `GM/GameClientComponent.cs:3997-4026` |
| AI | `PVPBots` | MMR → difficulty (GS bots, fallback) | `GM/BlackBoardComponent.cs:1877-1903` |
| AI | `BotNames` | bot display names | `GM/MultiplayerComponent.cs:600-617` |
| AI | `AllowedBots` | mercs bots may use | `GM/AiUtils.cs:23-50` |
| Meta (per user) | `BotMatchesInfo` | bot-match reward tables (first row) | `GM/MetagameComponent.cs:95, 1032, 1117-1120` |
| Meta | `AIOnboardingSpecs` | onboarding rewards, squads, UI flags | `GM/MetagameComponent.cs:103, 1031, 1518-1527`, `GM/AIOnboardingSpecs.cs` |
| Meta | `CreditsCurrencyMatchRewards`, `VersusCoinsMatchRewards` | PvP currency and daily caps (also caps offline Credits) | `GM/MetagameComponent.cs:97-99`, `GM/RewardComponent.cs:346-430` |
| Meta | `MatchmakingBotAddingTime` | GS lobby time before bots | `GM/GameMatchmakingComponent.cs:145, 644-650` |
| Meta | `MenuMatchInfos` | menu match tiles (`Type`, `Displayed`, `Locked`, `IsPVP`) | `GM/MainMenuComponent.cs:73, 360, 722-770` |
| Meta | `FTUE_Rewards_List`, `FTUETapReminder`, `FTUE_Conditions` | FTUE gates and rewards | `GM/MetagameComponent.cs:73-79, 864-893` |
| Meta | map table (`MapsComponent.Table`) | map list and modes | `GM/MapsComponent.cs:22-30` |
| Gameplay | `MatchTypeProbability`, `MatchTypeSpecs` | mode pick, mode list | `GM/GameplayComponent.cs:656-705`, `GM/MapsComponent.cs:27-28` |

"Per user" tables go through `PerUserDatabase`, which the CRM can override per player with `_OverrideTables`
(report 04). That is the data-only way to change bot rewards, bot difficulty or menu tiles without patching code.

---

## 11. Server dependencies of the existing offline modes

| Need | Why | Without it | Evidence |
|---|---|---|---|
| Connected, unpaused **TS link** (remote or Embedded) | `StartAutoMatchAsync` guard; `RequestOfflineBotsMatchRemote` runs on the TS | button does nothing | `GM/GameClientComponent.cs:3976-3979` |
| TS-side profile loaded (`World.GetProfileComponent(source)`) | parameters, rewards, `PVPActivated` | `MatchStarted:false`, falls through to GS | `GM/OfflineBotsGameplayComponent.cs:239-249` |
| TS game data (AI/Meta/Gameplay DB, maps) | parameters and rewards | NRE in RPC → error | §10 |
| Federation session open | `ClientComponent.Prepare` → `Authentication.Connect` for the offline link | offline level fails to connect | `EN/ClientComponent.cs:987-994` |
| Storage write + read-back | TS saves after `OfflineMatchAgainstBotsOver`; on resume it **reloads from storage** | rewards lost on resume | `GM/OfflineBotsGameplayComponent.cs:626`, `GM/GameServerComponent.cs:735-754`, report 03 |
| Leaderboard (optional) | `PostScore(profile,0)` on PvP activation | ignored (fire-and-forget, needs `LeaderboardId`) | `GM/LeaderboardsComponent.cs:112-126` |
| CRM refresh (optional) | after `ActivatePVP` | ignored | `GM/OfflineBotsGameplayComponent.cs:627-630` |
| Analytics/ads | tracking, post-match ad boost (`CanShowPostMatchAds` needs CRM pointcuts and `AllTimeOfflineVictories > 1`) | silently fail; disable ads | `GM/PlayerProfileComponent.cs:838-850`, `GM/GameCrmComponent.cs:43-56` |

No CRM/remote-config flag in C# gates offline bot matches. The only soft gates are data: `MenuMatchInfos`
(is the `BOTMATCH` tile displayed or locked?) and `BotMatchesAvailable` (always true, see §0.4). **[V]** for the
code; **[L]** that the shipped `MenuMatchInfos` shows the tile.

---

## 12. Recipes

### 12.1 Reach the stock bot modes (no game-code change)

Prerequisites from other reports: the Federation stub (02), storage (03), CRM/config with `"game"` (04),
`-ServerLocation Embedded -AnubisCommand QuickLaunch` and a super credential (06), all injected through `args.txt` (01).

1. Boot. The embedded TS starts in-process, and the client connects and goes straight to the menu (the tutorial is
   skipped under Embedded, §6). A new profile has `PVPActivated = false`.
2. **Onboarding bot matches:** tap "Battle" (`StartAutoMatch`). The TS returns an onboarding match (`TutoMatches` row
   = wins + 1), and the client loads `TrainingMatchWithBots` with the `Match<N>` bots. Each win gives trophies,
   onboarding rewards, MVP and merc XP. When the league leaves FTUE, the TS activates PvP.
3. **Free bot matches (any time):** use the `BOTMATCH` tile (`StartMatchWithBots`), or the developer cheat
   `PlayAgainstBots` (LOCAL). This gives a random BotMatch map, 3+4 bots, difficulty from `SoloBots`, and
   `BotMatchesInfo` rewards.
4. **Practice range:** merc screen → Practice (`Practice(mercId)`).
5. **After PvP activation, "Battle" needs a GS.** With Embedded, `ConnectGameplayServer(QuickMatchServerLink)` →
   LocalAnubis automatch → in-process GS → bots fill (§8). To make that instant for a single human, serve the CRM game
   parameters:

   ```json
   { "Matchmaking": { "MinPlayers": 1, "MaxOpenTimeInSeconds": 0, "EnableBotInLobby": true,
                      "NoobSettings": [ { "Id": "offline", "MatchCount": 1000000, "AffectMMR": true,
                                          "MaxLobbyTimeInSeconds": 1 } ] } }
   ```

   With `MinPlayers: 1`, `HasEnoughPlayerToStartMatch` is true as soon as the human is active. `CloseRoom` then runs
   after 0 s and fills to 8 (7 bots). **[V]** for the code path; **[L]** that LocalAnubis's Lobby honours
   `reserve "bot"` and `update room` (report 06).

To get developer cheats (force map/mode, `SkipTraining`, unlock cheats): the TS marks a player as developer when the
connection IP is in the edge list (`Config/EdgeList.txt` in data) or in CRM `AdditionalDeveloperIpRange`
(`GM/GameServerComponent.cs:976-993`, `Gameloft.Util/Geolocalization.cs:87-130`). With Embedded the IP is loopback, so
`"AdditionalDeveloperIpRange": "127.0.0.0/8"` should make the local player a developer **[L]** (the range syntax of
`IpRange.TryGetRange` was not checked). `IsValidDeveloper` also requires `IsDevelopment` (CRM `IsDevelopment`, default `true`).
The cheat menu shows LOCAL cheats to everyone, but opening it needs `NativeCanUseCheats` (developer)
(`GM/CheatsComponent.cs:415-451`).

### 12.2 Make full-reward bot matches first-class — option A: embedded GS (recommended)

Treat "bot match" as "PvP room with 1 human". It needs no Game.dll change and gives every progression effect in §5.5.
It is also the only path where other humans can later join and replace bots (P2P/LAN, report 06).

* UI: keep "Battle" as the entry point. Optionally repoint the `BOTMATCH` tile to the same flow by patching
  `NativeStartMatchWithBots` to call `StartAutoMatch`, or leave it as the light PvE mode.
* Config: §12.1 step 5 values. Raise `CreditsCurrencyMatchRewards` / `VersusCoinsMatchRewards` `DailyCap` through CRM
  table overrides if the daily caps feel restrictive offline.
* Divisions/seasons need leaderboard endpoints in the stub (report 02). Otherwise `PostScoreAsync` silently does nothing.
* Costs: one extra headless World per match on the device (CPU/RAM), and the GS lobby/room plumbing must work in
  LocalAnubis.

### 12.3 Option B: IL patch the offline bot path (no GS)

All sites are in `Game.OfflineBotsGameplayComponent` (Game.dll). The offsets are IL offsets as printed by
`ilspycmd --ilcode`. Prefer dnSpy or Mono.Cecil to raw byte edits. After patching, delete or regenerate
`lib/arm64-v8a/libaot-Game.dll.so` (the AOT image no longer matches the MVID, so the runtime falls back to JIT; report 02 §7) **[L]**.

| # | Method | Site | Change | Effect |
|---|---|---|---|---|
| B1 | `OnEndOfMatch` | `IL_0071..IL_0079` (`ldloc.1; callvirt get_PVPActivated; ldc.i4.0; ceq` before `brfalse.s IL_00ea`) | `ldc.i4.1` + `nop`s | client always sends real `agentScores` + `isMVP` (and shows trophies) |
| B2 | `ProcessRewards` | `IL_0000..IL_0009` (`ldarg.s profile; callvirt get_PVPActivated; ldc.i4.0; ceq`) → `stloc.0` | `ldc.i4.1` + 9 `nop` | MVP bonus and **merc mastery XP** for every bot match |
| B3 | `OnControlMessageReceived` | `IL_00e4..IL_00f7` (closure `profile.PVPActivated == false`) → `stloc.s 8` | `ldc.i4.1` + `nop`s | GentleTrophies on every bot match (and harmless re-`ActivatePVP`) |
| B4 | `OnControlMessageReceived` | `IL_032d: ldc.i4.0` (arg `giveDivisionTrophies` of `ApplyTrophiesResult` at `IL_032e`) | `ldc.i4.1` | division trophies (season progress) |
| B5 (opt.) | `PrepareOfflineMatchOnServer` | `IL_005b` and `IL_0081: ldc.i4.1` (arg `isOfflineMatch` of `Win`/`Lose`) | `ldc.i4.0` | PvP currency formula instead of `BotMatchesInfo`. **Caveat:** `GenerateCurrency` adds to the daily counters at generation time, for **both** pre-rolled lists, and `ProcessRewards` adds Credits again, so the cap fills about 2–3x too fast. Better: leave B5 out and boost the `BotMatchesInfo` win/lose tables through CRM `_OverrideTables`. |
| B6 (opt.) | `ShouldPlayAgainstBots` | `IL_002b..IL_0033` (`ldarg.1; callvirt get_PVPActivated; ldc.i4.0; ceq`) | `ldc.i4.1` | "Battle" always plays bots (offline-only build without GS). **Also** change `isInTraining` (`PrepareOfflineMatchOnServer IL_0052..IL_005a` for `Win` and `IL_0078..IL_0080` for `Lose`, `get_MatchConnectionType()==1`) to `InTutorialPhase(profile) && …`. Otherwise post-onboarding "Battle" matches get `GenerateOnboardingMatchRewards` → `null` → **zero rewards**. |

Still missing with option B (would need new code, for example a small helper method injected with Cecil):
`Wins/Kills/Deaths/Headshots/MVPs/Eliminations/AllTimePVPMatches`, session history/streaks, MMR, `CratesTrackWinsCount`,
TLE achievements (`GameMode.BotMatch` exclusion at `GM/TimeLimitedAchievementsComponent.cs:204,223`), and the full
achievement variant. The client would have to send kills/deaths/headshots in the JSON message (extend
`GetMatchResultMessageObject`), and the server would apply them like `GM/GameplayComponent.cs:855-883`.
Results remain client-trusted, which is irrelevant offline.

Optional extras:

* **Tutorial under Embedded:** `GameClientComponent.get_ShouldPlayTutorial` `IL_0041: ldc.i4.4; IL_0042: beq.s IL_004b`
  → `pop; nop; nop` (drops the `!= Embedded` test).
* **Bot count / team size:** `MultiplayerComponent.SpawnBotsHighestPlayerLevelMerc` uses literals `3` and `4`.
  `StartTrainingMatchWithBots` sets `NbOfflineBotsToSpawn = 7`.
* **Difficulty selector:** data-only. Override `SoloBots` (all rows = chosen difficulty) per user through CRM, or
  patch `botDifficultyFromDailyOfflineMatches`.

### 12.4 Data-only improvements (no patch, no GS)

* CRM `_OverrideTables` for `BotMatchesInfo` (bigger win/lose tables, crates), `SoloBots` (flat difficulty),
  `MenuMatchInfos` (show and unlock the `BOTMATCH` tile), `TutoMatches` (shorter onboarding, e.g. one row).
* `DailyCap` overrides on `CreditsCurrencyMatchRewards` (offline Credits share that cap).

---

## 13. Gotchas found while reading

* The **resume reload race**: the TS saves the bot-match result asynchronously and reloads the profile on resume. A
  slow storage write could lose the rewards. The local stub should make writes durable before answering
  (report 03). **[V]**
* `OfflineBotsGameplayComponent.MatchConnectionType` is a field of a **global component on the TS World** (shared by
  all players on that TS). It is harmless for one player per embedded TS. **[V]**
* `CreateBotWithProfile` consumes two `BotNames` entries per bot. The list reloads when empty, and duplicates get
  "(n)" suffixes. **[V]**
* Post-onboarding parameters leave `MatchStartTimerWithDuplicateMercs` / `RespawnTimerWithDuplicateMercs` at
  `Time.Zero`, and `OnGameplayStarted` applies them because `>= 0` holds. So duplicate-merc timers are 0 in bot
  matches. **[V]** for the code; **[L]** for the effect.
* `botDifficultyFromDailyOfflineMatches` dereferences `SoloBots` without a null check after checking `BotDifficulty`,
  so a missing table crashes `StartTrainingMatchWithBots`. **[V]**
* A pure "no TS at all" client (every link `ServerLocation.None`) loads a default profile locally
  (`GM/ProfileManagerComponent.cs:835-853`), and its RPCs would run locally (`EN/RemoteUtil.cs`). However, nothing
  loads `Menus.level` in that mode, and `StartAutoMatchAsync` refuses to run without a TS link. **[S]** This
  client-as-own-server route is not a shipped mode. Embedded is the sane base.

---

## 14. Answers to the brief

* **How is an offline bot match started?** Through the UI natives `StartAutoMatch` (only during onboarding) or
  `StartMatchWithBots` (always), from the menu while connected to a TS. There are no remote-config gates in code.
  The menu tile visibility comes from the `MenuMatchInfos` data. There is no connection-failure fallback. **[V]**
* **Maps, modes, bot counts, difficulties?** The mode set is TeamDeathMatch, ControlPoint, Uphold, Dominion
  (Survivor is unused; plus Tutorial and ShootingRange). Maps are data-driven (`AvailableInGameModes` contains
  `BotMatch`), with probabilities from `MatchTypeProbability`. Onboarding bots come from `Match<N>`. Bot matches use
  3+4 bots (4v4) at the player's best merc level and mod power. Difficulty comes from `SoloBots[dailyWins+1]` →
  `BotDifficulty` row. GS bots use the `PVPBots` MMR mapping plus terminator logic. **[V]** (map names need the data pack)
* **XP/currency/progression?** Onboarding matches: trophies, onboarding rewards, MVP and merc XP. Post-onboarding bot
  matches: only the `BotMatchesInfo` table (no trophies, MVP, merc XP, MMR or stats). GS PvP gives everything. To
  change it, use option A (embedded GS, recommended) or option B (IL sites B1–B4, optionally B5/B6). **[V]**
* **Server dependencies?** A TS (Embedded is fine), its profile and data, a Federation session, and storage
  save/reload. **[V]**
* **Does GameServerComponent run in-process for these matches?** For offline bot, practice and tutorial matches:
  **no**. The client World is master. The (embedded or remote) TS only brokers parameters and rewards. For
  bot-filled PvP: **yes**, through the embedded GS (`ServerManager` headless World). **[V]**
