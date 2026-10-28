using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm.Collector;

namespace McvsOffline.Server.Services.Social.LiveOps
{
    /// <summary>
    /// Builds the Osiris event objects the TS parses (OP/SocialEvent.cs, EN/TimeLimitedEventsComponent.cs
    /// RetrieveEventsAsync/ParseTemplate, and each type's Initialize). Every field below is one the parser or an
    /// Initialize reads; docs/LIVEOPS.md lists them with the code that reads them.
    /// <para>
    /// Template values are SEM "private" elements (<c>{"private":"1","value":&lt;raw&gt;}</c>: ParseElement returns the raw
    /// value), so the parsed template is exactly the object we mean. The rules that matter (a wrong one throws inside the
    /// TS, aborts RetrieveEventsAsync and leaves the menu waiting for its events):
    /// <list type="bullet">
    /// <item><c>title</c> is always a string: OnTimeLimitedEventsReceived calls <c>Title.Contains("DailyQuestTitle")</c>
    /// on every achievement event; daily quests are told apart by exactly that substring.</item>
    /// <item><c>banner_img</c>/<c>menu_img</c> are always strings (the implicit DownloadableAsset conversion calls
    /// <c>Trim()</c>); we use images that are in the data pack.</item>
    /// <item>every achievement has a known <c>primary_tracker</c> and at least one milestone with an integer
    /// <c>milestone_value</c> and rewards the client can parse (a null RewardItem throws in the claim);</item>
    /// <item><c>_version</c> changes whenever the content does: the TS keeps the stored copy of an event with the same
    /// revision and copies titles from the new one by index/milestone id, which throws when the shape changed.</item>
    /// <item>dates use <see cref="LiveOpsSchedule.WireDate"/> (culture-proof, see there).</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class LiveOpsEvents
    {
        public const string DivisionSeasonType = "DivisionSeasonEvent";
        public const string AchievementType = "TimeLimitedAchievementEvent";
        public const string DailyQuestType = "TimeLimitedDailyQuestEvent";
        public const string CompleteAll = "CompleteAllObjectives";

        /// <summary>A division season event: one per division, all with the season id as <c>_batch_tag</c>
        /// (GM/DivisionSeasonEvent.cs Initialize; GM/DivisionsComponent.cs picks the one whose Division is the player's).</summary>
        public static Dictionary<string, object> DivisionSeason(LiveOpsCatalog catalog, LiveOpsWindow season, int division)
        {
            var tuning = Obj();
            tuning["event_type"] = Private(DivisionSeasonType);
            tuning["title"] = Private(catalog.SeasonTitle);
            tuning["description"] = Private(catalog.SeasonDescription ?? "");
            var template = Obj();
            template["event_tuning"] = tuning;

            var leaderboard = Obj();
            leaderboard["name"] = LiveOpsSchedule.BoardName(season, division);
            leaderboard["order"] = "desc";
            var tournament = Obj();
            tournament["leaderboard"] = leaderboard;
            tournament["type"] = "none";            // TournamentMethod.None: no Osiris award, the TS rewards from PVPDivisionRewards
            tournament["awards"] = new List<object>();

            var e = Base(LiveOpsSchedule.DivisionEventId(season, division), season, template, 1);
            e["_batch_tag"] = LiveOpsSchedule.SeasonId(season);
            // DivisionSeasonEvent.Initialize: IndexOf("CurrentDivision == ") must be > 0, then int.Parse up to ')'.
            e["_participation"] = "(CurrentDivision == " + division.ToString(CultureInfo.InvariantCulture) + ")";
            e["tournament"] = tournament;
            return Finish(catalog, e, MiniJson.Serialize(template) + "|" + e["_participation"] + "|" + leaderboard["name"]);
        }

        /// <summary><see cref="DailyQuest(LiveOpsCatalog, LiveOpsWindow, CollectorRuntime)"/> without the collector (no Cosmetic crate).</summary>
        public static Dictionary<string, object> DailyQuest(LiveOpsCatalog catalog, LiveOpsWindow day)
        {
            return DailyQuest(catalog, day, null);
        }

        /// <summary>The day's quests (a TimeLimitedDailyQuestEvent): <c>count</c> objectives picked from the catalogue by
        /// the day, plus the "complete all" bonus (its goal is set by the TS to the number of other milestones). In a day
        /// that gets the Cosmetic crate (<see cref="CratesIn"/>) the collector's <c>dailyMission</c> follows the rewards
        /// of the first picked mission and its <c>dailyBonus</c> the bonus rewards (one crate per milestone, checked by
        /// <see cref="LiveOpsCatalog.ParseCollector"/>); otherwise the event, and so its <c>_version</c>, is exactly as
        /// without the collector.</summary>
        public static Dictionary<string, object> DailyQuest(LiveOpsCatalog catalog, LiveOpsWindow day, CollectorRuntime runtime)
        {
            var pool = new List<LiveOpsObjective>();
            foreach (LiveOpsObjective o in catalog.Objectives)
                if (o.DailyGoals != null && o.DailyRewards != null) pool.Add(o);
            if (pool.Count == 0 || catalog.DailyCount <= 0 || string.IsNullOrEmpty(catalog.DailyTitle)) return null;
            ulong state = LiveOpsHash.Fnv64("mcvs-offline/daily/v1|" + day.Key);
            List<LiveOpsObjective> picked = Pick(pool, MinInt(catalog.DailyCount, pool.Count), ref state);

            bool crates = CratesIn(catalog, day, runtime);
            var achievements = new List<object>();
            for (int i = 0; i < picked.Count; i++)
            {
                LiveOpsObjective o = picked[i];
                int goal = o.DailyGoals[LiveOpsHash.Below(ref state, o.DailyGoals.Count)];
                List<LiveOpsReward> rewards = o.DailyRewards;
                if (crates && i == 0 && catalog.Collector.DailyMission.Count > 0)
                {
                    rewards = new List<LiveOpsReward>(o.DailyRewards);
                    rewards.AddRange(catalog.Collector.DailyMission);
                }
                achievements.Add(Achievement(o.Tracker, o.Title, o.TitlePlural, new[] { goal }, new List<List<LiveOpsReward>> { rewards }));
            }
            var bonus = new List<LiveOpsReward>(catalog.DailyBonusRewards);
            if (crates) bonus.AddRange(catalog.Collector.DailyBonus);
            if (bonus.Count > 0)
                achievements.Add(Achievement(CompleteAll, catalog.DailyBonusTitle, catalog.DailyBonusTitle, new[] { picked.Count },
                                             new List<List<LiveOpsReward>> { bonus }));

            var template = AchievementTemplate(DailyQuestType, catalog.DailyTitle, catalog.DailyDescription, catalog.DailyBanner,
                                               catalog.DailyMenu, catalog.DailyAdvertisement, null, achievements);
            var e = Base(LiveOpsSchedule.DailyEventId(day), day, template, 1);
            return Finish(catalog, e, MiniJson.Serialize(template));
        }

        /// <summary><see cref="WeeklyEvent(LiveOpsCatalog, LiveOpsWindow, LiveOpsModifier, CollectorRuntime)"/> without the
        /// collector (no Cosmetic crate).</summary>
        public static Dictionary<string, object> WeeklyEvent(LiveOpsCatalog catalog, LiveOpsWindow season, LiveOpsModifier modifier)
        {
            return WeeklyEvent(catalog, season, modifier, null);
        }

        /// <summary>The season's achievement event (a TimeLimitedAchievementEvent over the season window): a themed
        /// objective plus <c>count - 1</c> others, three milestones each, goals scaled to the season length, and a
        /// "complete all" final reward with the season's event crate. In a season that gets the Cosmetic crate
        /// (<see cref="CratesIn"/>) the collector's <c>weeklyMilestones[i]</c> follow tier <c>i</c>'s rewards and its
        /// <c>weeklyFinal</c> follows the event crate; otherwise the event, and so its <c>_version</c>, is exactly as
        /// without the collector.</summary>
        public static Dictionary<string, object> WeeklyEvent(LiveOpsCatalog catalog, LiveOpsWindow season, LiveOpsModifier modifier,
                                                             CollectorRuntime runtime)
        {
            var pool = new List<LiveOpsObjective>();
            foreach (LiveOpsObjective o in catalog.Objectives)
                if (o.WeeklyGoals != null) pool.Add(o);
            if (pool.Count == 0 || catalog.WeeklyCount <= 0 || catalog.WeeklyMilestoneRewards.Count == 0) return null;
            long ordinal = LiveOpsSchedule.Ordinal(season);

            LiveOpsObjective theme = null;
            if (catalog.WeeklyThemes.Count > 0)
            {
                LiveOpsObjective candidate = catalog.Objective(catalog.WeeklyThemes[(int)Mod(ordinal, catalog.WeeklyThemes.Count)]);
                if (candidate != null && candidate.WeeklyGoals != null && !string.IsNullOrEmpty(candidate.ThemeTitle)) theme = candidate;
            }
            if (theme == null)
            {
                foreach (LiveOpsObjective o in pool)
                {
                    if (!string.IsNullOrEmpty(o.ThemeTitle))
                    {
                        theme = o;
                        break;
                    }
                }
            }
            if (theme == null) return null;

            ulong state = LiveOpsHash.Fnv64("mcvs-offline/weekly/v1|" + season.Key);
            var others = new List<LiveOpsObjective>();
            foreach (LiveOpsObjective o in pool)
                if (!ReferenceEquals(o, theme)) others.Add(o);
            var picked = new List<LiveOpsObjective> { theme };
            picked.AddRange(Pick(others, MinInt(catalog.WeeklyCount - 1, others.Count), ref state));

            bool crates = CratesIn(catalog, season, runtime);
            List<List<LiveOpsReward>> tiers = catalog.WeeklyMilestoneRewards;
            if (crates)
            {
                // ParseCollector checked there is one collector list per tier.
                tiers = new List<List<LiveOpsReward>>();
                for (int i = 0; i < catalog.WeeklyMilestoneRewards.Count; i++)
                {
                    var tier = new List<LiveOpsReward>(catalog.WeeklyMilestoneRewards[i]);
                    if (i < catalog.Collector.WeeklyMilestones.Count) tier.AddRange(catalog.Collector.WeeklyMilestones[i]);
                    tiers.Add(tier);
                }
            }
            var achievements = new List<object>();
            int milestones = 0;
            foreach (LiveOpsObjective o in picked)
            {
                int[] goals = ScaleGoals(o.WeeklyGoals, season.Days);
                milestones += goals.Length;
                achievements.Add(Achievement(o.Tracker, o.Title, o.TitlePlural, goals, tiers));
            }
            var final = new List<LiveOpsReward>(catalog.WeeklyFinalRewards);
            if (catalog.WeeklyFinalEventCrate && catalog.EventCrates.Count > 0)
                final.Add(new LiveOpsReward { Item = catalog.EventCrates[(int)Mod(ordinal, catalog.EventCrates.Count)], Quantity = 1 });
            if (crates) final.AddRange(catalog.Collector.WeeklyFinal);
            if (final.Count > 0)
                achievements.Add(Achievement(CompleteAll, catalog.WeeklyFinalTitle, catalog.WeeklyFinalTitle, new[] { milestones },
                                             new List<List<LiveOpsReward>> { final }));

            bool boosted = modifier != null && (modifier.Cells.Count > 0 || modifier.RivalFactor != 1.0);
            var template = AchievementTemplate(AchievementType, theme.ThemeTitle, theme.ThemeDescription ?? "", catalog.WeeklyBanner,
                                               catalog.WeeklyMenu, catalog.WeeklyAdvertisement,
                                               boosted && !string.IsNullOrEmpty(catalog.WeeklyBoostedTag) ? catalog.WeeklyBoostedTag : null,
                                               achievements);
            var e = Base(LiveOpsSchedule.WeeklyEventId(season), season, template, 2);
            // The tag is cosmetic and left out of the revision: switching liveops.modifiers mid-season must not make
            // the TS replace (and reset) the event the player is working on. Same shape, so keeping the stored copy is safe.
            var untagged = JsonValue.DeepCloneObject(template);
            JsonPath.RemoveKey((Dictionary<string, object>)untagged["event_tuning"], "tag_text");
            return Finish(catalog, e, MiniJson.Serialize(untagged));
        }

        /// <summary>Weekly goals are tuned for 7 days: other season lengths scale them (kept ascending, at least 1).</summary>
        public static int[] ScaleGoals(List<int> goals, int days)
        {
            var result = new int[goals.Count];
            int previous = 0;
            for (int i = 0; i < goals.Count; i++)
            {
                int g = (int)Math.Round(goals[i] * days / 7.0);
                if (g < 1) g = 1;
                if (g <= previous) g = previous + 1;
                result[i] = g;
                previous = g;
            }
            return result;
        }

        /// <summary>
        /// True when the event of <paramref name="window"/> (a quest day or a season) carries the Cosmetic crate: the
        /// catalogue has a valid <c>collector</c> block and <see cref="CollectorRuntime.CratesInWindow"/> allows the
        /// window's start (enabled, collector.json valid, store patch not known missing, start ≥ <c>since</c>). The one
        /// question live ops ask the collector (§2.12): the CRM layer is decided from the same inputs.
        /// </summary>
        public static bool CratesIn(LiveOpsCatalog catalog, LiveOpsWindow window, CollectorRuntime runtime)
        {
            if (runtime == null || catalog == null || catalog.Collector == null || window == null) return false;
            try
            {
                return runtime.CratesInWindow(window.Start, catalog.Collector.Since);
            }
            catch (Exception)
            {
                // The runtime does not throw on its inputs (it keeps the last collector.json read). Anything else costs
                // the crate, never the event; LiveOpsService logs it when it reads the runtime's InputsKey.
                return false;
            }
        }

        // ------------------------------------------------------------------------------------------ helpers

        private static Dictionary<string, object> AchievementTemplate(string type, string title, string description, string banner, string menu,
                                                                      string advertisement, string tag, List<object> achievements)
        {
            var tuning = Obj();
            tuning["event_type"] = Private(type);
            tuning["title"] = Private(title ?? "");
            tuning["description"] = Private(description ?? "");
            tuning["banner_img"] = Private(banner ?? "");
            tuning["menu_img"] = Private(menu ?? "");
            tuning["advertisement_text"] = Private(advertisement ?? "");
            if (!string.IsNullOrEmpty(tag)) tuning["tag_text"] = Private(tag);
            var template = Obj();
            template["event_tuning"] = tuning;
            template["milestones"] = Private(achievements);
            return template;
        }

        private static Dictionary<string, object> Achievement(string tracker, string title, string titlePlural, int[] goals,
                                                              List<List<LiveOpsReward>> tiers)
        {
            var milestones = new List<object>();
            for (int i = 0; i < goals.Length; i++)
            {
                var m = Obj();
                m["milestone_value"] = (long)goals[i];
                var rewards = new List<object>();
                foreach (LiveOpsReward r in tiers[MinInt(i, tiers.Count - 1)])
                {
                    var reward = Obj();
                    reward["item"] = r.Item;
                    reward["quantity"] = (long)r.Quantity;
                    rewards.Add(reward);
                }
                m["rewards"] = rewards;
                milestones.Add(m);
            }
            var a = Obj();
            a["primary_tracker"] = tracker;
            a["progression_title"] = title ?? "";
            a["progression_title_plural"] = titlePlural ?? title ?? "";
            a["milestones"] = milestones;
            return a;
        }

        private static Dictionary<string, object> Base(string id, LiveOpsWindow window, Dictionary<string, object> template, int priority)
        {
            var e = Obj();
            e["id"] = id;
            e["name"] = id;
            e["start_date"] = LiveOpsSchedule.WireDate(window.Start);
            e["end_date"] = LiveOpsSchedule.WireDate(window.End);
            e["_priority"] = (long)priority;
            e["_template"] = MiniJson.Serialize(template);
            return e;
        }

        private static Dictionary<string, object> Finish(LiveOpsCatalog catalog, Dictionary<string, object> e, string content)
        {
            int revision = LiveOpsHash.Revision("mcvs-offline/event/v1|" + catalog.Revision.ToString(CultureInfo.InvariantCulture) + "|"
                                                + e["start_date"] + "|" + e["end_date"] + "|" + content);
            e["_version"] = revision.ToString(CultureInfo.InvariantCulture);
            return e;
        }

        private static List<LiveOpsObjective> Pick(List<LiveOpsObjective> pool, int count, ref ulong state)
        {
            var copy = new List<LiveOpsObjective>(pool);
            var result = new List<LiveOpsObjective>();
            for (int i = 0; i < count && copy.Count > 0; i++)
            {
                int at = LiveOpsHash.Below(ref state, copy.Count);
                result.Add(copy[at]);
                copy.RemoveAt(at);
            }
            return result;
        }

        private static int MinInt(int a, int b)
        {
            return a < b ? a : b;
        }

        private static long Mod(long value, int n)
        {
            long m = value % n;
            return m < 0 ? m + n : m;
        }

        private static Dictionary<string, object> Private(object value)
        {
            var p = Obj();
            p["private"] = "1";
            p["value"] = value;
            return p;
        }

        private static Dictionary<string, object> Obj()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }
    }
}
