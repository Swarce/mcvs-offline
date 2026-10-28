using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Golden
{
    /// <summary>
    /// The golden file of economy WP-0 (<see cref="GoldenScenarios"/>): <c>{name: {sha256, length}}</c> for every output of
    /// the scenarios, captured on ac64bfd, before any collector wiring. Regenerating it is a deliberate, reviewed act:
    /// <c>MCVS_WRITE_GOLDEN=1 dotnet test --filter FullyQualifiedName~GoldenCaptureTests</c> on the commit the file is
    /// named after.
    /// </summary>
    public class GoldenCaptureTests
    {
        private readonly ITestOutputHelper output;

        public GoldenCaptureTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>Captures every scenario twice (the text must not vary between runs) and writes the golden file.</summary>
        [Fact]
        public void Write()
        {
            if (Environment.GetEnvironmentVariable("MCVS_WRITE_GOLDEN") != "1")
            {
                output.WriteLine("MCVS_WRITE_GOLDEN is not 1: " + GoldenScenarios.FileName + " left as it is");
                return;
            }
            List<GoldenOutput> outputs = GoldenScenarios.RunAll();
            Assert.Equal(GoldenScenarios.Names(), outputs.Select(o => o.Name).ToList());
            foreach (GoldenOutput o in outputs)
            {
                bool eventOff = !o.LiveOps && (o.Kind == GoldenScenarios.Daily || o.Kind == GoldenScenarios.Weekly);
                Assert.True(o.Status == (eventOff ? 404 : 200), o.ToString());
                Assert.False(string.IsNullOrEmpty(o.Text), o.Name);
            }
            AssertCaptureIsMeaningful(outputs);

            List<GoldenOutput> again = GoldenScenarios.RunAll();
            for (int i = 0; i < outputs.Count; i++) Assert.True(outputs[i].Text == again[i].Text, outputs[i].Name + " differs between two runs");

            File.WriteAllText(GoldenScenarios.FilePath, GoldenScenarios.Serialize(outputs), new UTF8Encoding(false));
            output.WriteLine(outputs.Count + " entries written to " + GoldenScenarios.FilePath);
        }

        /// <summary>Every scenario has an entry (and nothing else does): the file was captured from these scenarios.</summary>
        [Fact]
        public void FileIsComplete()
        {
            Dictionary<string, GoldenEntry> golden = GoldenScenarios.Load();
            List<string> names = GoldenScenarios.Names();
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain(names, n => !golden.ContainsKey(n));
            Assert.DoesNotContain(golden.Keys, k => !names.Contains(k));
            foreach (var kv in golden)
            {
                Assert.Matches(new Regex("^[0-9a-f]{64}$"), kv.Value.Sha256 ?? "");
                Assert.True(kv.Value.Length > 0, kv.Key);
            }
            // The file lists the entries in capture order, one per line, as Write produces them.
            string text = File.ReadAllText(GoldenScenarios.FilePath, Encoding.UTF8);
            Assert.Equal(names, Regex.Matches(text, "^  \"([^\"]+)\": ", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList());
        }

        /// <summary>
        /// The scenarios cover what they claim (checked at capture time only: the collector packages change some of it
        /// on purpose): live ops announce the day's events, the inbox item is in, the unlock-all player's store leaves out
        /// the owned signatures, and the other two dumps get the whole store.
        /// </summary>
        private static void AssertCaptureIsMeaningful(List<GoldenOutput> outputs)
        {
            var byName = outputs.ToDictionary(o => o.Name, StringComparer.Ordinal);
            foreach (DateTime day in GoldenScenarios.Days())
            {
                foreach (bool liveOps in GoldenScenarios.LiveOpsModes)
                {
                    GoldenOutput doc = byName[GoldenScenarios.NameOf(liveOps, day, GoldenScenarios.Static, null)];
                    var root = MiniJson.ParseObject(doc.Text);
                    var news = JsonPath.Get(root, "newsfeeds") as List<object>;
                    Assert.True(news != null && news.Count == 1, doc.Name + ": the inbox item");
                    Assert.Equal(CrmNewsfeed.NewsId, JsonValue.GetString(news[0] as Dictionary<string, object>, "news_id"));
                    var participation = JsonPath.Get(root, "groupware.events.participation") as List<object>;
                    var ids = (participation ?? new List<object>()).Select(e => JsonValue.GetString(e as Dictionary<string, object>, "id")).ToList();
                    string daily = byName[GoldenScenarios.NameOf(liveOps, day, GoldenScenarios.Daily, null)].EventId;
                    string weekly = byName[GoldenScenarios.NameOf(liveOps, day, GoldenScenarios.Weekly, null)].EventId;
                    if (liveOps)
                    {
                        Assert.Contains(daily, ids);
                        Assert.Contains(weekly, ids);
                    }
                    else
                    {
                        Assert.Null(participation);
                    }
                    Assert.Equal(MiniJson.Serialize(JsonPath.Get(root, "game")),
                                 byName[GoldenScenarios.NameOf(liveOps, day, GoldenScenarios.Game, null)].Text);
                    if (!GoldenScenarios.IsPlayerDay(day)) continue;
                    Assert.Equal(doc.Text, byName[GoldenScenarios.NameOf(liveOps, day, GoldenScenarios.Player, GoldenScenarios.Fresh)].Text);
                    Assert.Equal(doc.Text, byName[GoldenScenarios.NameOf(liveOps, day, GoldenScenarios.Player, GoldenScenarios.Malformed)].Text);
                    string unlockAll = byName[GoldenScenarios.NameOf(liveOps, day, GoldenScenarios.Player, GoldenScenarios.UnlockAll)].Text;
                    Assert.DoesNotContain("\"KillSignature_Avatar_1512\"", unlockAll);
                    Assert.DoesNotContain("\"KillSignature_Background_35\"", unlockAll);
                    Assert.Contains("\"KillSignature_Sound_5\"", unlockAll);
                    Assert.Contains("\"KillSignature_Avatar_1512\"", doc.Text);
                }
            }
        }
    }
}
