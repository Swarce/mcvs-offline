using System.Collections.Generic;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Social;
using Xunit;

namespace McvsOffline.Tests.Misc
{
    /// <summary>
    /// The template rules mirror EN/TimeLimitedEventsComponent.cs ParseTemplate/ParseElement: anything they would
    /// throw on must be caught here, because one throwing event aborts the TS's whole event list.
    /// </summary>
    public class TleTemplateValidatorTests
    {
        /// <summary>Templates the TS's parser accepts (also checked against the real parser by ClientConformanceTests).</summary>
        public static readonly string[] Accepted =
        {
            "{}",
            "{\"event_tuning\":{}}",
            "{\"event_tuning\":{\"event_type\":{\"private\":\"1\",\"value\":\"ShopCrateV2Event\"}}}",
            "{\"event_tuning\":{\"title\":{\"type\":\"text\",\"value\":{\"en\":\"Hi\",\"fr\":\"Salut\"}}}}",
            "{\"event_tuning\":{\"plain\":{\"type\":\"text_plain\",\"value\":\"x\"}}}",
            "{\"event_tuning\":{\"pick\":{\"type\":\"list_single\",\"value\":{\"a\":{\"private\":\"1\",\"value\":3}}}}}",
            "{\"event_tuning\":{\"pick\":{\"type\":\"list_single\",\"value\":[{\"type\":\"date\",\"value\":{\"date\":\"2026-10-25\"}}]}}}",
            "{\"event_tuning\":{\"items\":{\"type\":\"list_append\",\"value\":[{\"itemtype\":\"Crate\",\"id\":\"MainLootCrate\",\"consumable\":true,\"value\":2}]}}}",
            "{\"event_tuning\":{\"multi\":{\"type\":\"list_multi\",\"value\":{\"a\":\"\",\"b\":\"\"}}}}",
            "{\"event_tuning\":{\"range\":{\"type\":\"range\",\"value\":[1,5]}}}",
            "{\"event_tuning\":{\"len\":{\"type\":\"duration\",\"value\":{\"duration\":3600}}}}",
            "{\"event_tuning\":{\"len\":{\"type\":\"duration\",\"value\":{\"duration\":\"3600\"}}}}",
            "{\"event_tuning\":{\"item\":{\"itemtype\":\"Crate\",\"consumable\":true}}}",
            "{\"event_tuning\":{\"item\":{\"itemtype\":\"Crate\",\"consumable\":\"False\",\"value\":\"x\"}}}",
            "{\"event_tuning\":{\"at\":{\"type\":\"time\",\"value\":{\"time\":\"anything: SafeGet swallows it\"}}}}",
            "{\"event_tuning\":{\"group\":{\"sub\":{\"x\":{\"type\":\"text_plain\",\"value\":\"y\"}}}}}",
            "{\"event_tuning\":{\"ignored\":5,\"also\":[1,2]},\"milestones\":{\"sub\":{}}}",
        };

        public static IEnumerable<object[]> AcceptedData()
        {
            foreach (string t in Accepted) yield return new object[] { t };
        }

        /// <summary>Templates the TS's parser throws on (also checked against the real parser by ClientConformanceTests).</summary>
        public static readonly string[] Rejected =
        {
            "not json",
            "[]",
            "{\"event_tuning\":{\"x\":{\"type\":\"color\",\"value\":1}}}",
            "{\"event_tuning\":{\"x\":{\"type\":5}}}",
            "{\"event_tuning\":{\"x\":{\"type\":\"text\",\"value\":{\"en\":{\"nested\":1}}}}}",
            "{\"event_tuning\":{\"x\":{\"type\":\"list_multi\",\"value\":\"scalar\"}}}",
            "{\"event_tuning\":{\"x\":{\"type\":\"list_append\",\"value\":[{\"type\":\"nope\"}]}}}",
            "{\"event_tuning\":{\"x\":{\"type\":\"time\",\"value\":3600}}}",
            "{\"event_tuning\":{\"x\":{\"type\":\"duration\",\"value\":[1]}}}",
            "{\"event_tuning\":{\"x\":{\"type\":\"duration\",\"value\":{\"duration\":\"long\"}}}}",
            "{\"event_tuning\":{\"x\":{\"type\":\"date\",\"value\":\"2026-10-25\"}}}",
            "{\"event_tuning\":{\"x\":{\"sub\":[{\"a\":1}]}}}",
            "{\"event_tuning\":{\"x\":{\"private\":{\"a\":1}}}}",
            "{\"event_tuning\":{\"x\":{\"itemtype\":\"Crate\",\"consumable\":\"yes\"}}}",
            "{\"event_tuning\":{\"x\":{\"itemtype\":\"Crate\",\"consumable\":true,\"value\":null}}}",
            "{\"event_tuning\":{\"x\":{\"itemtype\":\"Crate\",\"consumable\":true,\"value\":\"2.5\"}}}",
            "{\"event_tuning\":{\"x\":{\"type\":\"duration\",\"value\":{\"duration\":null}}}}",
            "{\"event_tuning\":{\"x\":{\"itemtype\":\"Crate\",\"id\":{\"a\":1}}}}",
            "{\"milestones\":{\"type\":\"bogus\"}}",
        };

        public static IEnumerable<object[]> RejectedData()
        {
            foreach (string t in Rejected) yield return new object[] { t };
        }

        private static string Problem(string template, string participation = null)
        {
            var e = new Dictionary<string, object> { ["id"] = "e", ["_template"] = template };
            if (participation != null) e["_participation"] = participation;
            return TleTemplateValidator.Problem(e);
        }

        [Theory]
        [MemberData(nameof(AcceptedData))]
        public void TemplatesTheTsParsesAreAccepted(string template)
        {
            Assert.Null(Problem(template));
        }

        [Theory]
        [MemberData(nameof(RejectedData))]
        public void TemplatesTheTsWouldThrowOnAreRejected(string template)
        {
            Assert.NotNull(Problem(template));
        }

        [Fact]
        public void DivisionSeasonParticipationMustCarryAnIntegerDivision()
        {
            const string season = "{\"event_tuning\":{\"event_type\":{\"private\":\"1\",\"value\":\"Game.DivisionSeasonEvent\"}}}";
            Assert.Null(Problem(season, "(CurrentDivision == 3)"));
            Assert.Null(Problem(season, "nothing to parse")); // Initialize just returns false
            Assert.NotNull(Problem(season, "(CurrentDivision == three)"));
            // Other event types do not parse _participation that way.
            Assert.Null(Problem("{\"event_tuning\":{\"event_type\":{\"private\":\"1\",\"value\":\"ShopCrateV2Event\"}}}", "(CurrentDivision == three)"));
        }

        [Fact]
        public void NormalizeDropsEventsThatWouldBreakTheTs()
        {
            var bad = MiniJson.ParseObject("{\"_template\":\"{\\\"event_tuning\\\":{\\\"x\\\":{\\\"type\\\":\\\"nope\\\"}}}\"}");
            Assert.Null(SocialEvents.Normalize("bad", bad, null));
            var good = MiniJson.ParseObject("{\"_template\":{\"event_tuning\":{}},\"start_date\":\"2026-01-01T00:00:00Z\",\"end_date\":\"2027-01-01T00:00:00Z\"}");
            Assert.Equal("good", SocialEvents.Normalize("good", good, null)["id"]);
        }
    }
}
