# Live ops: weekly division seasons, daily quests, weekly achievement events, Special Events

What the offline server runs so that the game has seasons, missions and events again, how the game consumes them,
and how to change the schedule. Owner: MISC/CONTENT (`server/Services/Social/LiveOps/`, `server/Services/Leaderboards/SeasonRivals.cs`,
the CRM layer in `server/Services/Crm/`, `tools/content-gen/liveops_tuning.json` → `server/Content/liveops.json`).
Paths like `GM/...` are relative to the decompiled assemblies (`GM` = Game.dll, `EN` = Gameloft.Engine, `OP` =
Gameloft.OnlinePlatform), as in the research reports.

Everything is a function of the clock and the settings: nothing is stored by the server, so a restart, a reinstall of
the server or weeks without playing change nothing. Progress (quests, division trophies, rewards) lives in the
player's profile, saved by the game as always.

## 1. What the player gets

| Feature | Rhythm | In the game |
|---|---|---|
| **Division season** | every Monday 00:00 UTC (7 days) | Divisions screen: a league group of 100 (the player + 99 rivals whose trophies grow during the week). At the season end the game promotes (top zone), keeps or demotes the player by one division and sends the end-of-season reward to the inbox. |
| **Daily missions** | every day 00:00 UTC | "DAILY MISSIONS": 3 missions picked from 6 (matches, wins, kill shots, eliminations, assists, MVP) with VersusCoins + Credits, and a "complete all" bonus: the game's own *Daily mission loot crate* (`Crate_Contracts`). |
| **Weekly event** | same window as the season | a limited-time event with a theme (kill shots, wins, eliminations or assists in turn) and 4 objectives × 3 milestones (VersusCoins, Credits, upgrade tokens), plus a final reward for completing everything: 20 diamonds and one of the 65 cosmetic *event crates* (`Crate_Event_<n>`, rotating weekly). The next week's event is announced during the last day. |
| **Special Event** | a new mode every day at the daily reset (`arenaHours`) | Battle › Special Event: an Arena mode (Havoc, Blitz, Scorecharge, Competitive best of 3, Override, Ambush, Grand Bounty, Bountiful or a plain mode) with bots as in Classic and full match rewards, wins ×1.25 (Competitive ×1.5) in Credits and VersusCoins; the next mode's timer on the box (§8a). |
| **Weekly modifier** | one per season, rotating | standard / Coin rush (VersusCoins ×1.5) / Trophy fever (division trophies ×1.5) / Credit rush (Credits ×1.5) / MVP bounty (MVP bonus ×3) / Rival surge (rivals ×1.2, VersusCoins and Credits ×1.25). Daily caps scale with the rewards. Visible in the division perks (daily cap, MVP bonus) and as a "MATCH REWARDS BOOSTED!" tag on the weekly event. |

From `collector.since` (2026-11-09) the daily missions, the weekly event and the season rewards also hand out the
**Cosmetic crate** ("CUSTOMIZATION CRATE", [ECONOMY.md](ECONOMY.md) §5): one with the day's first mission (the first
picked; on the phone the mission whose card shows the crate, not necessarily the first row), one on tier 1
and tier 2 and two on tier 3 of each weekly objective, and on ranks 4–30 at the season end. Placement, `since` and the
guard: §7a.

Permanent achievements: **nothing to restore**. In 1.17.32 they are disabled in the client itself:
`GM/AchievementsComponent.cs` `NativeGetAchievements` returns null and `GetRewards`/`GetRewardsObjects` return empty
lists, and the `Achievements_General`/`_Tank`/`_Assassins`/`_Assault`/`_Disruptors` tables it would load are not in
the data pack (only their texts remain in `Localization/Achiev_*.json`). The time-limited achievement system
(`GM/TimeLimitedAchievementsComponent.cs`) replaced them; that is what the weekly events and daily missions use.

## 2. How the game learns about events

1. **CRM lists the ids.** `StandardCrm.Data.DeserializeTLEs` reads `groupware.events.participation[].id` (and
   `advertisement[].id`). `GM/GameTimeLimitedEventsComponent.cs AddPersistentEvents` adds the profile's current
   division season id (`DivisionsComponent.Data.Id`) and the id of every achievement event kept in the profile
   (`TleAchievements`). An id is "participating" (`CanParticipate`) only if CRM lists it.
2. **The TS asks Osiris for them**: `GET events?event_ids=a,b,…&language=xx` (`EN/TimeLimitedEventsComponent.cs
   RetrieveEventsAsync`), once when its CRM document is set and again whenever CRM is refetched.
3. **Each answer** goes through `new SocialEvent(item)` (OP/SocialEvent.cs: `id`, `name`, `start_date`, `end_date`,
   `_version` → Revision, `tournament.leaderboard.{name,order}`, `tournament.type`, every other key into `Fields`),
   `ParseTemplate(Fields["_template"].Value<string>())`, `TypeUtil.GetType(event_type)` and the type's `Initialize`. An
   exception that is not an online error aborts the whole list and the menu waits for its events forever, which is
   why every event is generated defensively and verified with the real client (§9).
4. **Refresh at the right moment.** The CRM document carries `expiry` = the next schedule step (next daily reset,
   season end or the weekly announcement). `EN/CRMComponent.cs OnConstantUpdate` calls `OnCrmExpired` →
   `RefreshCrmConfig` when it passes, which re-runs step 2: a game left running over midnight gets the new missions,
   and over Monday the new season. The next season's division events are listed one season ahead as well, so
   `OnEventsStarted` (fired from the TS's own timers) starts the season on time even if the refresh is late.
   The served expiry is always at least 2 s in the future (a past expiry would make the TS refetch CRM every frame).

## 3. Schedule

| Setting (`settings.json`, `"liveops": {...}`) | Default | Meaning |
|---|---|---|
| `enabled` | `true` | `false` = no ids in CRM, no expiry, no modifiers, events answered as unknown: the game as before. |
| `seasonStart` | `"2024-01-01T00:00:00Z"` (a Monday) | Any season start (truncated to the hour, UTC). Seasons repeat every `seasonDays` from it, also backwards. |
| `seasonDays` | `7` | 1–28. Weekly-event goals scale with it (×days/7); the rivals' targets are per day, so they scale too. With 1 the ids carry the bare start (`mcvs_season_2026102500_d05`), otherwise `<start>x<days>`. |
| `dailyResetHour` | `0` | UTC hour (0–23) at which the daily missions change. |
| `divisionSeasons`, `dailyQuests`, `weeklyEvents` | `true` | Switch one part off (its ids leave CRM). |
| `modifiers` | `"rotate"` | `"rotate"` (season ordinal mod rotation), `"off"`, or a modifier id (`coinRush`, `trophyFever`, …) every week. |
| `difficulty` | `1.0` | Scales every rival's score (0.1–10): 0.5 = half the matches for promotion. |
| `seasonRewardMultiplier` | `1.0` | Multiplies the end-of-season reward quantities (`PVPDivisionRewards`). |
| `arenaEvents`, `arenaRotation`, `arenaHours`, `arenaMap` | `true`, `"rotate"`, `24`, `"Random"` | The Special Events: §8a.2. |

Ids embed their window (`yyyyMMddHH` start + `x<days>`), so an event or board stays what it was even after the
settings change: `mcvs_season_2026101900x7` (season id = `_batch_tag`), `mcvs_season_2026101900x7_d05` (division 5's
season event), `mcvs_division_2026101900x7_d05_Leaderboard` (its board; the TS reads `…_Leaderboard_QA` for a
developer profile, which every loopback player is, both forms are served), `mcvs_daily_2026102500`,
`mcvs_weekly_2026101900x7`.

Dates on the wire are `"2026-10-19 00:00:00Z"` (the .NET `u` pattern), deliberately **without the ISO `T`**: Newtonsoft
turns a `yyyy-MM-ddT…` string into a DateTime token, and SocialEvent re-reads it through `JValue.ToString()` in the
phone's culture before `DateTime.TryParse(…, InvariantInfo)`. On a day-first locale `2026-10-05T00:00:00Z` comes back
as **10 May** (shown by `LiveOpsConformanceTests.IsoDatesWouldDependOnThePhonesCulture`). A string without the `T`
stays a string and parses identically everywhere.

**Changing the schedule.** Edit `settings.json` (in the embedded build: `<GlfRootPath>/McvsOffline/settings.json`) and
restart the game, or wait for the next CRM fetch. Changing `seasonStart`/`seasonDays` starts a new season at once
(the TS sees a new season id): the old season is evaluated from its own board as usual. `GET /_mcvs/liveops` shows
the current season, day, modifier, CRM expiry, the listed ids and today's objectives.

## 4. Division seasons (what the TS does)

`GM/DivisionsComponent.cs`, on the TS, when events arrive or start (`OnEventsReceived`), if `PVPActivated`:

* It takes the in-progress `DivisionSeasonEvent` whose `Division` equals the player's division. `DivisionSeasonEvent.Initialize`
  needs `Fields._batch_tag` (the season id) and `Fields._participation` containing `"CurrentDivision == <n>)"` at an
  index > 0 (we send `"(CurrentDivision == 5)"`), and the board name comes from `tournament.leaderboard.name`
  (`tournament.type: "none"`, no Osiris awards). One such event per division (17) per season, all listed in CRM.
* Same season id as the profile's `Data.SeasonId` → nothing to do. Different → **season end**
  (`OnNewSeasonStartedServer`): `GET leaderboards/desc/<old board>?limit=0` (SelfData); with `my_entry.rank > 0`,
  `TieredDivision.GetStatusForRank(rank, total_entries)`: promoted if `rank ≤ ⌊PromotionZone% × total⌋`, demoted if
  `rank ≥ total − ⌊DemotionZone% × total⌋ + 1` (one division at a time); the reward of that rank
  (`PVPDivisionRewards`/`PVPDivisionRewardsType`, ranks 1–60: tier crates for 1–30, 120 Credits for 31–60) goes **into
  the inbox** (`SendRewardToInbox`, "DIVISION REWARD", 30 days), `DivisionTrophies` is reset to 0, `Data` becomes the
  new season and the profile is saved. The UI then shows the season-end screen.
* **Exactly once**: the transition happens when the season id changes and the new `Data` is saved with it. **Catch-up**:
  after weeks away, `Data` still names the last season played; that board is still served, its rivals frozen at
  their final scores, so the player gets that season's result once and joins the current season. Seasons not played
  give nothing (as on the live servers: no board entry, no rank). A 5xx from the board would retry later; we never send one.
* A **fresh profile** (or the user's existing one, whose `Data` was never set because no events existed) starts its
  first season with no evaluation; division trophies collected before are reset by the game at that moment.
* During the season the TS posts `DivisionTrophies` after each match (`LeaderboardsComponent.PostScoreAsync`, only
  while `now < Data.EndDate`) with `_level` and the other custom fields.

Division trophies per match (`GM/GameplayComponent.cs ApplyTrophiesResult`): a win pays `DivisionTrophies.Win`
(0 in every 1.17.32 division) `+ PlayerLevelXP.DivisionTrophiesBonus(level)` (= the player level, at least 2), a
loss `DivisionTrophies.Lose` (0 up to Gold, 10–25 from Platinum on), a draw `DivisionTrophies.Draw` (0).

## 5. Rivals and the one-week scaling

The original season length is **not in the client**: the dates came from the live Osiris events. What the client
bounds it by: the "Season ends in 3 days" push (`GameNotificationRuleSet kSeason_EndClose`, disabled), a "season
ending soon" push 2 h before the end (`kDivision_End_Soon`), Olympus league groups created with a 30-day expiry
(`Leaderboard.CreateLeague(groupSize: 100, expiration: 2592000)`) and 30-day reward messages; the weekly-event push
texts ("Weekly Events" category) point to a weekly rhythm for events. Rather than scale an unknown length, the rival
targets are defined **per day of season** from how much the game itself expects a day of play to be (the Credits and
VersusCoins daily caps are reached after about 13–20 wins a day), so any `seasonDays` keeps the same daily effort.

A season board (`SeasonRivals`, used for every `mcvs_division_*` board) is a group of 100: the real player(s) plus
rivals. The rival on the promotion line (rank ⌊PromotionZone% × 100⌋; Master: rank 10) ends the season with

    S = matchesPerDay[division] × seasonDays × (winTrophies(level) + lossTrophies) / 2 × difficulty × rivalFactor

i.e. as many trophies as a player at a 50 % win rate who plays `matchesPerDay` matches a day. `level` is the player's
own `_level` on that board, so a level-46 profile (46 trophies a win) faces proportionally bigger numbers than a
level-5 one. The best rival ends with 2.2 × S, the rival on the demotion line with 0.15 × S, the last with 0; ±6 %
jitter, then clamped so the lines are exact: **promoted iff the player's trophies ≥ S, demoted iff below 0.15 × S**
(ties rank real players first). Rival i shows `final_i × x^a_i` (x = elapsed fraction of the season, a_i ∈ [0.8, 1.25]),
so the promotion line climbs during the week and is final at the end; after the end the board is frozen.
Zones and trophies are read at run time from the player's data pack (`Design/MCVS_Meta.xlsx`: `DivisionsList`,
`PlayerLevelXP`); without it (fresh install, before the download) the fallback is 20 %/10 % and a win worth the level.

| Div | Name | Promotion / demotion zone | Loss trophies | Matches/day | 7-day line (matches ≈ wins×2) | Line score at level 10 / 46 | Safe line at 46 |
|---|---|---|---|---|---|---|---|
| 1 | Starter | 50 % / 0 % | 0 | 2 | 14 | 70 / 322 | — |
| 2 | Bronze 1 | 40 % / 5 % | 0 | 3 | 21 | 105 / 483 | 72 |
| 3 | Bronze 2 | 40 % / 10 % | 0 | 3.5 | 24.5 | 123 / 564 | 84 |
| 4 | Bronze 3 | 40 % / 10 % | 0 | 4 | 28 | 140 / 644 | 96 |
| 5 | Silver 1 | 30 % / 10 % | 0 | 4.5 | 31.5 | 158 / 725 | 108 |
| 6 | Silver 2 | 30 % / 10 % | 0 | 5 | 35 | 175 / 805 | 120 |
| 7 | Silver 3 | 30 % / 10 % | 0 | 5.5 | 38.5 | 193 / 886 | 132 |
| 8 | Gold 1 | 20 % / 15 % | 0 | 6 | 42 | 210 / 966 | 144 |
| 9 | Gold 2 | 20 % / 15 % | 0 | 6.5 | 45.5 | 228 / 1047 | 157 |
| 10 | Gold 3 | 20 % / 15 % | 0 | 7 | 49 | 245 / 1127 | 169 |
| 11 | Platinum 1 | 15 % / 20 % | 10 | 7.5 | 52.5 | 525 / 1470 | 220 |
| 12 | Platinum 2 | 15 % / 20 % | 12 | 8 | 56 | 616 / 1624 | 243 |
| 13 | Platinum 3 | 15 % / 20 % | 15 | 8.5 | 59.5 | 744 / 1815 | 272 |
| 14 | Diamond 1 | 10 % / 25 % | 17 | 9 | 63 | 851 / 1985 | 297 |
| 15 | Diamond 2 | 10 % / 25 % | 20 | 10 | 70 | 1050 / 2310 | 346 |
| 16 | Diamond 3 | 10 % / 25 % | 25 | 11 | 77 | 1348 / 2734 | 410 |
| 17 | Master | — / 30 % | 25 | 12 (rank 10) | 84 | 1470 / 2982 | 447 |

So a week of Starter takes about 7 wins, Gold about 20–25, Diamond 3 about 40 wins (with the loss trophies, ~77
matches at 50 %); staying put takes about a sixth of that. The numbers are ours (`seasons` in
`tools/content-gen/liveops_tuning.json`); `liveops.difficulty` scales them all at run time. Other boards (not
`mcvs_division_*`) keep the static rivals of `leaderboards.virtualTopScore`.

## 6. Daily missions

A `TimeLimitedDailyQuestEvent` per day (`mcvs_daily_<yyyyMMddHH>`, 24 h). The TS tells daily missions from other
achievement events by `Title.Contains("DailyQuestTitle")` (`GM/TimeLimitedAchievementsComponent.cs
OnTimeLimitedEventsReceived`), so the title is the loc id `crm.DailyQuestTitle`. Only in-progress daily events are
added to the profile; only one is unlocked at a time (`SetAchievementAvailable`: while yesterday's has an unclaimed
completed mission, today's waits until it is claimed). The milestones are a flat list (no pools, so the TS's random
`SelectRandomAchievementInPool` does nothing): 3 objectives picked by the day (FNV-1a + xorshift over the date) from
the 6 daily objectives, goal from its list, rewards from the catalogue, plus a `CompleteAllObjectives` bonus (the TS
sets its goal to the number of other milestones). Progress is counted by the game on the server Worlds
(`BaseAchievementsComponent.OnMatchResult` and the match-stat handlers) for any PvP match; no mode or merc
restriction is used, so every mission can be done in a quick match.

## 7. Weekly achievement events

A `TimeLimitedAchievementEvent` per season (`mcvs_weekly_<season>`), title/description from the game's TLE texts
(`menu.Tle01Name`/`Tle01Description`, `Tle02Name`/`TleWinDescription`, `TleTitle`/`TleKillsDescription`,
`Tle05Name`/`AssistsDescription`), banner/menu images from the data pack (`Menus/assets/png/tournament/*`; the
implicit `DownloadableAsset(string)` conversion would throw on a missing one), the theme objective + 3 others picked by
the season, 3 milestones each (goals × seasonDays/7), and a `CompleteAllObjectives` final with 20 HardCurrency and
`Crate_Event_<ordinal mod 65>` (cosmetics). No unlock cost, no user duration, no shards (mega events need the shard
economy and the final-milestone purchase, not used). An event whose rewards are not claimed when it ends stays in the
profile until claimed (`MustKeepEventAliveAfterEnd`); its id is still served.

## 7a. The Cosmetic crate in the events

`liveops.json` carries a `collector` block (from `tools/content-gen/liveops_tuning.json`):

```json
"collector": {
  "since": "2026-11-09T00:00:00Z",
  "dailyBonus": [],
  "dailyMission": [{"item": "Crate_Event_Special", "quantity": 1}],
  "weeklyMilestones": [[{"item": "Crate_Event_Special", "quantity": 1}],
                       [{"item": "Crate_Event_Special", "quantity": 1}],
                       [{"item": "Crate_Event_Special", "quantity": 2}]],
  "weeklyFinal": []
}
```

* **Placement.** The lists are appended to the day's first mission, the daily bonus, weekly tier *i* and the weekly
  final. A milestone may hold only one crate: the game's claim reveals only the last crate of a milestone (the other's
  items are granted unseen), so content-gen and the server refuse the Cosmetic crate on the daily bonus
  (`Crate_Contracts`), on the weekly final (the event crate) or twice in one list. A broken block is logged and
  ignored; live ops run on without crates.
* **`since`** (a Monday 00:00 UTC on the season grid): only windows that **start** at or after it get the crate, and
  only while the collector can run (`economy.collector.enabled`, a valid `collector.json`, the `store` patch). Earlier
  windows keep a byte-identical template and `_version`. Release with `since`, **never by bumping `revision`**: a bump
  replaces the running day and week for everybody (§9). Toggling `economy.collector.enabled` replaces the windows from
  `since` once, too (the settings page asks first); so does a settings.json the server cannot parse, which counts as
  off.
* **The guard and its lead.** The TS sees tomorrow's missions all day and the next weekly event during the last day of
  a season, so the first crate-carrying window is in its hands up to a day before `since`. From `since − 1 day` (and
  for good once `FindEvent` served an event with the crate: the latch in `<data>/collector-guard.json`) the CRM
  document always gives the crate at least the 5 guard cells, so it is never empty. Details:
  [design/economy-collector.md](design/economy-collector.md) §2.12, §3.
* `GET /_mcvs/liveops` shows `collector: {phase, reason, since, activeFor, block, cratesServed}`.

## 8. Modifiers

What the game offers for "season modifiers":

* **`_OverrideTables` cells in CRM** (used): the TS applies `game._OverrideTables`, the GS (which computes match
  rewards and trophies) `game._ServerConfig.default._OverrideTables` (the builder mirrors them). A MUL cell (`"*1.5"`)
  on an existing row (CRM cannot add rows). The season's modifier cells are combined with the static economy
  multipliers (`economy.*`: one cell, product of both) and change at the season boundary with the CRM refresh.
* **`ArenaModeEvent`** (used for the Special Events, §8a, not as a modifier): `EndMatchRewardMultipliers`, restricted
  agents, map, match type, but only for the separate Arena mode the player has to select: the **Special Event** box of
  Battle's "Select game mode" popup (`GameClientComponent IsArenaSelected` → `filters._arena`), carried in the
  matchmaker room kwargs (`default_room_kwargs._arenaParams`). The in-process LocalAnubis already copies
  `default_room_kwargs` into the room (`LA/Lobby.cs:338-350`), so no LocalAnubis change was needed; Classic matches
  never see the multipliers. Without an `ArenaModeEvent` the Special Event box stays locked; live ops now generates
  them (§8a), so it unlocks with a countdown while `liveops.arenaEvents` is on. Client traps (the generator and the
  validator cover them): `img` is mandatory (a missing one aborts the whole event list), the agent list must be
  non-empty and without "Any", and the windows must be contiguous (a gap leaves the GS without `_arenaParams`).
* **`BonusMultiplierEvent`** (not used): only the texts of the ad-reward "boost" banner; ads do not exist offline.
* The **trophy curve** itself: `PlayerLevelXP.DivisionTrophiesBonus` (trophies per win) and `DivisionsList`
  `DivisionTrophies.*` are table values, so a modifier can scale them (Trophy fever).

| Id | Cells (MCVS_Meta) | Rivals |
|---|---|---|
| `standard` | – | ×1 |
| `coinRush` | `VersusCoinsMatchRewards` Victory/Defeat/MVP/DailyCap ×1.5 | ×1 |
| `trophyFever` | `PlayerLevelXP.DivisionTrophiesBonus` ×1.5 | ×1 (so promotion takes 1/1.5 the wins) |
| `creditRush` | `CreditsCurrencyMatchRewards` Victory/Defeat/MVP/DailyCap ×1.5 | ×1 |
| `mvpBounty` | MVPAmount ×3 (VersusCoins and Credits) | ×1 |
| `rivalSurge` | VersusCoins and Credits rewards and caps ×1.25 | ×1.2 |

Rotation: `standard, coinRush, trophyFever, creditRush, mvpBounty, rivalSurge` by whole seasons since 2024-01-01
(the week of 2026-10-19 is `trophyFever`, the next `creditRush`). Change the list or the factors in `liveops_tuning.json`, or pin one with
`liveops.modifiers`.

## 8a. Special Events (Arena)

The Battle popup has two boxes: **Classic multiplayer** and the **Special Event** (`menu.ArenaTitle` "ARENA EVENT" /
`menu.ArenaDescription` "SPECIAL EVENT"). The second is the game's **Arena mode** (`GameMode.Arena`), driven by one
time-limited `ArenaModeEvent`; it unlocks only while such an event is in progress, listed in CRM participation
(`CanParticipate`), and the player owns (or trials) one of the agents it allows (`GM/GameClientComponent.cs
GetArenaUIInfo`, `MeetsArenaAgentRequirements`). There is no level gate or CRM switch. Before build 15 the server never
served one, so the box was always locked. Research and code references: the match-modes notes (`special-event.md`,
`verify.md`).

### 8a.1 What runs

Live ops list a **contiguous chain** of Special Events: the window in progress and the next one, always both (ids in
`groupware.events.participation`, the CRM `expiry` at the latest at the window's end; in the last 2 s of a window, the
one after the next too and the next window's end, §8a.3). A window is
`liveops.arenaHours` long (default 24) on a grid of whole windows from 2024-01-01 `dailyResetHour`:00 UTC, so 24 h
windows change with the daily missions. Ids carry window and mode: `mcvs_arena_2026102100x24h_havoc`.

The mode of a window is picked from the 23 modes of `liveops.json` `arena.modes`, which content-gen builds from the
data pack's Arena variants (the `Arena.*` loc ids and the `MCVS_Gameplay` `Arena*` rule tables; every table and every
titled variant must be used by a mode or named in `unusedTables`/`unusedTexts`):

| Mode ids | Match type | Rule tables (`MatchParameterSets`) | Wins pay |
|---|---|---|---|
| `havoc` (TDM), `override` (Zone Control), `ambush` (Warzone), `grandBounty`, `bountiful` (Bounty) | fixed | `ArenaHavoc`, `ArenaOverride`, `ArenaAmbush`, `ArenaGrandBounty`, `ArenaBountiful` | ×1.25 |
| `blitzTDM`, `blitzZoneControl`, `blitzWarzone`, `blitzBounty` | the four modes | `ArenaBlitz` (core charge ×2) | ×1.25 |
| `scorechargeTDM`, `…ZoneControl`, `…Warzone`, `…Bounty` | the four modes | `ArenaScorecharge<Mode>` (charge only from kills, captures, chips) | ×1.25 |
| `competitiveTDM`, `…ZoneControl`, `…Warzone`, `…Bounty`, `competitiveRegular` (random mode per map) | fixed / `None` | `Arena3RoundSystem` (best of 3), no duplicate agents | ×1.5 |
| `tdm`, `zoneControl`, `warzone`, `bounty` | fixed | – (the plain mode) | ×1.25 |
| `tripleElixir` (pinned only: it has no title or rules text of its own) | TDM | `ArenaTripleElixir` (core charge ×3) | ×1.25 |

"Wins pay" is the event's `CreditsVictoryAmountMultiplier` and `VersusCoinsVictoryAmountMultiplier` (defeat and MVP
×1): see [ECONOMY.md](ECONOMY.md) §4. Every event allows all 21 agents as an explicit `Merc_1 … Merc_21` list, plays on
a random PvP map (or the one `liveops.arenaMap` names; never Barrage, Bridge or Downtown, whose levels are not in the
data pack), shows the match type's tournament icon (`Menus/assets/png/tournament/mode_*.png`) and leaves gear and the
level bonus as in Classic (`ForcedMercModTier` false).

**Rotation** (`liveops.arenaRotation: "rotate"`): every rotating mode once per cycle of 22 windows, the cycle shuffled
by its number, never the same mode twice in a row (when a cycle would begin with the previous cycle's last mode, its
first two swap). A function of the window alone: nothing is stored.

### 8a.2 Settings

| Setting | Default | Meaning |
|---|---|---|
| `liveops.arenaEvents` | `true` | The Special Events. Needs `liveops.enabled`. |
| `liveops.arenaRotation` | `"rotate"` | `"rotate"`, `"off"` (none), or a mode id: always that mode (the cheapest "pick your mode" bot match with full rewards, e.g. `competitiveWarzone`, or `tdm` with `arenaMap`). An unknown id rotates (a warning in the log). |
| `liveops.arenaHours` | `24` | 1–168. The window length. |
| `liveops.arenaMap` | `"Random"` | `"Random"` (each mode's own map: random) or `Snow`, `Desert`, `Favelas`, `Rooftop`, `DogDays`, `Port`, `Stadium`, `Stadium2` for every Special Event. |

All four are on the settings page (Live ops), applied at the next CRM fetch, with one exception (§8a.3).

### 8a.3 The client's three traps, and how the server avoids them

1. **`img` is mandatory.** `ArenaModeEvent.Initialize` turns it into a `DownloadableAsset` (`value.Trim()`): without it
   Initialize throws a NullReferenceException, RetrieveEventsAsync rethrows it and **no** event (seasons, missions)
   reaches the menu.
2. **The agent list must be a non-empty list of `<name>_<id>` strings, never `"Any"` or `"MercClass_…"`.** A
   `MercClass_` entry throws in Initialize (`int.Parse`). An empty list, or one with "Any", becomes
   `RestrictedAgents = null`: the C# then treats the event as unrestricted and `GetArenaUIInfo` hands the Flash box
   `arenaEventRestrictedAgents = null`, whose `.length` `MatchTypeBox.setup` reads (a TypeError in the lobby).
   (The research expected an ArgumentNullException in `GetArenaUIInfo`; the real client, Android and PC, does not throw:
   its LINQ chain is null-conditional. `LiveOpsArenaConformanceTests` shows both.)
3. **No gap between windows.** The client never clears its current Arena event (`UpdateCurrentAndNextArenaEvent` only
   assigns), so after an event ends the box stays open; the TS, when the player starts, takes the first event **in
   progress** for the room (`AddArenaEventParametersToArgs`). With none, the room gets `_arena` without `_arenaParams`
   and the GS throws (`GameServerComponent` reads `Room["_arenaParams"]` unguarded).

The server's answers: every Arena event goes through `TleTemplateValidator` (img, agents, match type, map, rule
tables, multipliers, booleans, loc ids), also the hand-written ones of `content/social/events.json`, which are dropped
when they fail; content-gen and the catalogue parser check the same rules on every mode (a broken mode is left out, a
broken `arena` block costs only the Special Events); the CRM lists an Arena event only when the current and the next
one pass and form a chain (`TleTemplateValidator.ArenaChainProblem`); the next window is always listed, so at a window
change the TS starts it on its own timer (`OnEventsStarted`) even before the CRM refresh. The chain is checked at the
real time and at the document's expiry: a document served in the last 2 s of a window (its expiry is pushed to the next
window's end so that the TS reads it before it expires) lists three events, the one in progress, the next and the one
after, because the TS replaces its list on every fetch and must still have the event the client holds.

**Hand-written Arena events** (`content/social/events.json`, listed with `crm.eventIds`) get the per-event checks
only (img, agents and the rest above), not the chain check: nothing lists a successor for them. When the last one ends
while the game runs (and no generated Special Event is in progress), trap 3 applies until the game restarts. So such
events must cover the whole session or be followed, without a gap, by another one; when `social/events.json` is read
the log says where their chain ends, and warns about a gap or an overlap between them.

**Switching off.** Because of trap 3, Special Events cannot stop while the game runs once the TS has been handed one:
`liveops.arenaEvents: false`, `arenaRotation: "off"` or `liveops.enabled: false` then keep the chain going, pinned to the
mode of the window that was in progress, until the game restarts (the server lives in the game's process; its memory
of the served event ends with it). The log says so once (`Special Events are switched off, but the game already has
one`), and `/_mcvs/liveops` shows `arena.kept: true`. Before the TS has seen one, off is off at the next CRM fetch.
(The standalone host is the exception: a game that outlives a server restart keeps its event; restart the game too.)

### 8a.4 Matchmaking and bots

Nothing Arena-specific: Battle → Special Event → lobby → Start goes through the same group-play automatch as Classic
with the filter `_arena = true` (`GameClientComponent.ConnectGameplayServer`). The TS adds
`default_room_kwargs._arenaParams` (the event's `ArenaParams`, JSON), the in-process LocalAnubis creates the room from
them (`Lobby.cs`), and the GS's public `CloseRoom` branch fills it with bots exactly as in Classic: the CRM's
`_ServerConfig.default.Matchmaking` `MinPlayers` 1 and `MaxOpenTimeInSeconds` 0 (`instantBots`, on by default) have no
mode check. Bots pick from the event's agents (`AiUtils`: all 21) and, in the competitive modes, avoid duplicates in a
team. A Special Event match is a public PvP match: trophies, division, Credits and VersusCoins (with the event's
multipliers, within the daily caps), MVP, agent XP and the daily and weekly objectives, as in Classic.
`LiveOpsArenaTests.TheCrmListsThisWindowAndTheNextAndTheEventsAreServed` checks the instant-bot cells in the same
document that lists the events. Not device-tested yet (§11).

### 8a.5 Diagnostics

`GET /_mcvs/liveops` → `arena`: `listed`, `kept`, `current` and `next` (id, mode, loc ids, match type, rule tables, map,
agent count, win multipliers, window), `upcoming` (the modes of the seven windows after the next), `modes` (`*`: outside
the rotation), `maps`, `problem` (why the catalogue has fewer or no modes); `settings` carries the four arena keys.

## 9. Safety rules (why the TS cannot choke on these events)

* Template values are SEM `{"private":"1","value":…}` elements, so `ParseTemplate` returns exactly our values; every
  event also passes `TleTemplateValidator` before it is served.
* Always strings: `title`, `description`, `banner_img`, `menu_img`, `advertisement_text` (the TS calls
  `Title.Contains` and `string.Trim()` on them). Every achievement has a known `primary_tracker`, `progression_title(_plural)`
  loc ids (the UI formats them with the goal) and ascending integer `milestone_value`s; every reward is a type the client
  parses (`RewardHelper.CreateRewardItem` returns null otherwise and the claim would throw).
* `_version` is a hash of the content (without the cosmetic tag): the TS keeps a stored event with the same revision and
  copies titles into it by index/milestone id, which throws if the shape changed; a new revision makes it replace the
  stored copy instead. (So a content update resets the progress of the events running at that moment.)
* `content-gen` validates every id against the player's data (crates, tables/fields, images, loc ids) and refuses
  bad tuning. `LiveOpsConformanceTests` runs 16 days of generated CRM documents and events through the real client:
  `StandardCrm.Data`, `Json.Load`, `SocialEvent`, `ParseTemplate`, `TypeUtil.GetType`, each type's `Initialize` (with
  `RewardHelper` creating every reward), then the TS's later unguarded reads; under a day-first culture.
* Special Events (`ArenaModeEvent`): their own three traps and the chain rule, §8a.3; `LiveOpsArenaConformanceTests`
  runs 14 days of them through the real client, its `GetArenaUIInfo` included.
* `liveops.enabled: false` is the kill switch (for Special Events once the game has one: at the next start, §8a.3).

## 10. Tuning without code

* `tools/content-gen/liveops_tuning.json`: objectives (tracker, loc ids, goals, rewards), daily count and bonus, weekly
  themes, milestone rewards and final reward, modifiers and rotation, the rival targets (`seasons.promotionMatchesPerDay`,
  `topFactor`, `safeFraction`), images, the `collector` block (§7a). Then `python3 -I tools/content-gen/content_gen.py`
  and rebuild. Bump `revision` when a change must replace events players already hold (not for the Cosmetic crate: move
  `collector.since` instead; content-gen warns when it is less than 7 days after the build).
* Or drop an edited `liveops.json` into `<data>/content/` (read on the next request; no rebuild).

## 11. Device checklist

- [ ] Boot with live ops on: the TS logs `GET events?event_ids=mcvs_season_…` (39 ids: 37 and the two Special Events) and reaches the menu (no wait).
- [ ] Divisions screen: season timer counts down to Monday 00:00 UTC; the board shows 100 entries, the player among
      rivals; after a match the player's score rises (`POST leaderboards/desc/mcvs_division_…_Leaderboard_QA/me`).
- [ ] Daily missions screen shows 3 missions + bonus; a quick match advances them; claiming pays; the next day (or
      after midnight with the game open: `GET configs/users/me` at the expiry) brings new ones.
- [ ] Weekly event screen: theme title, 4 objectives × 3 milestones, final reward; images shown (or at least no crash).
- [ ] Season end (set `liveops.seasonStart` one hour in the future of a season boundary, or wait for Monday): the
      season-end popup, the "DIVISION REWARD" inbox message, promotion when above the line.
- [ ] Modifier week: division perks show the boosted daily cap; a win pays more (Coin rush / Credit rush).
- [ ] The Cosmetic crate from `since` on: STATUS.md, build 9, checks 14–28.
- [ ] Special Event (§8a): Battle shows the box unlocked with the mode's title and match type (e.g. "TEAM DEATHMATCH:
      HAVOC" / "TEAM DEATHMATCH") and an end timer; its lobby shows the rules text and the 21 agent icons; Start fills the
      room with 7 bots at once (as Classic) and the match is the event's mode on a valid map; no GS exception about
      `_arenaParams` in the logs; the end screen pays more Credits and VersusCoins for a win than Classic does (×1.25,
      ×1.5 in a Competitive mode, up to the daily cap); trophies and the daily/weekly objectives advance.
- [ ] Special Event window change (set `liveops.arenaHours` 1, or play over midnight UTC): the box switches to the next
      mode without a restart; with the game open across the change the next match still starts.
- [ ] `liveops.arenaRotation` set to a mode (e.g. `competitiveWarzone`): that mode after the next match; the
      competitive best of three really has three rounds and no duplicate agents in a team.
- [ ] `liveops.arenaEvents` off while the game runs: the box stays usable (log line "switched off, but the game already
      has one"); after a restart it is locked.
- [ ] Descriptions with `{0_…}` placeholders (TLE event descriptions): check how the SWF renders them.
