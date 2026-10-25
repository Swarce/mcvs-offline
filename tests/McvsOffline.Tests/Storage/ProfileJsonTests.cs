using System.Collections.Generic;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Storage;
using Xunit;

namespace McvsOffline.Tests.Storage
{
    /// <summary>Tree operations over profile documents with verbatim nodes, and the span scanner behind the file format.</summary>
    public class ProfileJsonTests
    {
        private static string[] P(string path)
        {
            Assert.True(ProfileJson.TryParsePath(path, out var segments));
            return segments;
        }

        [Fact]
        public void PathsSplitOnDotsOnlyAndRejectEmptySegments()
        {
            Assert.Empty(P(""));
            Assert.Empty(P(null));
            Assert.Equal(new[] { "_mcvs", "gameProfile" }, P("_mcvs.gameProfile"));
            Assert.Equal(new[] { "$default", "/Profile", "Version" }, P("$default./Profile.Version"));
            foreach (string bad in new[] { "a..b", ".a", "a." })
                Assert.False(ProfileJson.TryParsePath(bad, out _));
        }

        [Fact]
        public void VerbatimTextIsTrimmedButOtherwiseExact()
        {
            var v = VerbatimJson.FromRequestText("\uFEFF \r\n{\"a\": 1.50, \"b\":\"\\u00e9\"}\t\n");
            Assert.True(v.IsJson);
            Assert.Equal("{\"a\": 1.50, \"b\":\"\\u00e9\"}", v.Text);
            Assert.True(v.IsObject);
            Assert.Equal("é", JsonPath.Get(v.Value, "b"));

            var opaque = VerbatimJson.FromRequestText("{broken");
            Assert.False(opaque.IsJson);
            Assert.Equal("{broken", opaque.Text);
            Assert.Equal("{broken", opaque.Value);
            Assert.False(VerbatimJson.FromRequestText("").IsJson);
        }

        [Fact]
        public void ReadsGoThroughVerbatimObjectsAndReturnTheStoredNode()
        {
            var root = ProfileJson.NewObject();
            var dump = VerbatimJson.FromRequestText(GameData.GameProfileDump);
            ProfileJson.Set(root, P("_mcvs.gameProfile"), dump);

            Assert.True(ProfileJson.TryGet(root, P("_mcvs.gameProfile"), out var node));
            Assert.Same(dump, node);
            Assert.Equal(GameData.GameProfileDump, ProfileJson.ResponseText(node));
            Assert.True(ProfileJson.TryGet(root, P("_mcvs.gameProfile.$default./Profile.Version"), out var version));
            Assert.Equal(57L, version);
            Assert.True(ProfileJson.TryGet(root, P("_mcvs.gameProfile.$default./Profile.List.1"), out var item));
            Assert.Equal(2.5, item);
            Assert.False(ProfileJson.Exists(root, P("_mcvs.gameProfile.nope")));
            // Keys with dots inside the dump are not addressable by path, but they are kept.
            Assert.False(ProfileJson.Exists(root, P("_mcvs.gameProfile.$default./Profile.Attachments.'0'")));
            Assert.Contains("\"Attachments.'0'\"", ProfileJson.Serialize(root));
        }

        [Fact]
        public void WritingInsideAVerbatimObjectMaterializesACopy()
        {
            var root = ProfileJson.NewObject();
            var section = VerbatimJson.FromRequestText("{\"a\":1,\"b\":{\"c\":2}}");
            root["s"] = section;

            ProfileJson.Set(root, P("s.b.d"), 3L);
            Assert.IsType<Dictionary<string, object>>(root["s"]);
            Assert.Equal("{\"s\":{\"a\":1,\"b\":{\"c\":2,\"d\":3}}}", ProfileJson.Serialize(root));
            // The original verbatim value is untouched (it is shared and immutable).
            Assert.Equal("{\"a\":1,\"b\":{\"c\":2}}", section.Text);
            Assert.False(((Dictionary<string, object>)section.Value)["b"] is Dictionary<string, object> b && b.ContainsKey("d"));
        }

        [Fact]
        public void RemoveKeepsOrderAndDoesNotMaterializeForAbsentPaths()
        {
            var root = MiniJson.ParseObject("{\"a\":1,\"b\":2,\"c\":3}");
            var verbatim = VerbatimJson.FromRequestText("{\"x\":1}");
            root["v"] = verbatim;
            Assert.False(ProfileJson.Remove(root, P("v.missing")));
            Assert.Same(verbatim, root["v"]);
            Assert.True(ProfileJson.Remove(root, P("b")));
            root["d"] = 4L;
            Assert.Equal("{\"a\":1,\"c\":3,\"v\":{\"x\":1},\"d\":4}", ProfileJson.Serialize(root));
            Assert.True(ProfileJson.Remove(root, P("v.x")));
            Assert.Equal("{\"a\":1,\"c\":3,\"v\":{},\"d\":4}", ProfileJson.Serialize(root));
        }

        [Fact]
        public void DeepMergeRecursesIntoObjectsAndReplacesEverythingElse()
        {
            var target = MiniJson.ParseObject("{\"_mcvs\":{\"gameProfile\":{\"k\":1},\"snapshot\":{\"a\":1,\"b\":[1,2]}},\"level\":1,\"arr\":[1]}");
            ((Dictionary<string, object>)target["_mcvs"])["currentMatch"] = VerbatimJson.FromRequestText("{\"m\":1}");
            var patch = MiniJson.ParseObject("{\"_mcvs\":{\"snapshot\":{\"b\":[3],\"c\":null},\"currentMatch\":{\"n\":2}},\"level\":2,\"arr\":{\"o\":1},\"new\":{\"x\":{}}}");
            ProfileJson.DeepMerge(target, patch);
            Assert.Equal("{\"_mcvs\":{\"gameProfile\":{\"k\":1},\"snapshot\":{\"a\":1,\"b\":[3],\"c\":null},\"currentMatch\":{\"m\":1,\"n\":2}},"
                         + "\"level\":2,\"arr\":{\"o\":1},\"new\":{\"x\":{}}}", ProfileJson.Serialize(target));
            // No aliasing: changing the patch afterwards does not change the target.
            ((Dictionary<string, object>)patch["new"])["x"] = 5L;
            Assert.Equal("{\"x\":{}}", ProfileJson.Serialize(target["new"]));
        }

        [Fact]
        public void ProjectionNestsAndSharesVerbatimNodes()
        {
            var root = MiniJson.ParseObject("{\"level\":3,\"_mcvs\":{\"snapshot\":{\"MatchMakingRating\":1000,\"x\":1}}}");
            ((Dictionary<string, object>)root["_mcvs"])["gameProfile"] = VerbatimJson.FromRequestText(GameData.GameProfileDump);
            var projected = ProfileJson.Project(root, new[] { "_mcvs.snapshot.MatchMakingRating", "_mcvs.gameProfile", " level ", "", "a..b", "nope" });
            Assert.Equal("{\"_mcvs\":{\"snapshot\":{\"MatchMakingRating\":1000},\"gameProfile\":" + GameData.GameProfileDump + "},\"level\":3}",
                         ProfileJson.Serialize(projected));
            // Projections are copies: editing one leaves the source alone.
            ((Dictionary<string, object>)((Dictionary<string, object>)projected["_mcvs"])["snapshot"])["MatchMakingRating"] = 1L;
            Assert.Equal(1000L, JsonPath.Get(root, "_mcvs.snapshot.MatchMakingRating"));
        }

        [Fact]
        public void SerializationIsDeterministicAndMatchesMiniJsonForPlainValues()
        {
            var value = MiniJson.Parse("{\"b\":[1,-2,3.5,1e300,true,null,\"\\u2028x\"],\"a\":{\"big\":123456789012345678901234567890,\"e\":{},\"l\":[]}}");
            Assert.Equal(MiniJson.Serialize(value), ProfileJson.Serialize(value));
            Assert.Equal(MiniJson.Serialize(value, true), ProfileJson.Serialize(value, true));
            Assert.Equal(ProfileJson.Serialize(value), ProfileJson.Serialize(ProfileJson.Clone(value)));
        }

        [Fact]
        public void OpaqueTextIsAJsonStringInsideDocuments()
        {
            var root = ProfileJson.NewObject();
            root["t"] = VerbatimJson.FromOpaqueText("a \"b\" \n");
            Assert.Equal("{\"t\":\"a \\\"b\\\" \\n\"}", ProfileJson.Serialize(root));
            Assert.Equal("a \"b\" \n", ProfileJson.ResponseText(root["t"]));
        }

        [Fact]
        public void ToPlainAndDeepEqualsLookThroughVerbatimNodes()
        {
            var root = ProfileJson.NewObject();
            root["v"] = VerbatimJson.FromRequestText("{\"a\":[1,{\"b\":2}]}");
            var plain = (Dictionary<string, object>)ProfileJson.ToPlain(root);
            Assert.IsType<Dictionary<string, object>>(plain["v"]);
            Assert.True(ProfileJson.DeepEquals(root, plain));
            Assert.True(ProfileJson.DeepEquals(root, MiniJson.Parse("{\"v\":{\"a\":[1,{\"b\":2}]}}")));
            Assert.False(ProfileJson.DeepEquals(root, MiniJson.Parse("{\"v\":{\"a\":[1,{\"b\":3}]}}")));
        }

        [Fact]
        public void CollectVerbatimFindsEveryVerbatimNodeWithItsPath()
        {
            var root = MiniJson.ParseObject("{\"a.b\":{\"c\":1}}");
            ProfileJson.Set(root, P("_mcvs.gameProfile"), VerbatimJson.FromRequestText("{}"));
            ((Dictionary<string, object>)root["a.b"])["d"] = VerbatimJson.FromOpaqueText("x");
            var found = ProfileJson.CollectVerbatim(root);
            Assert.Equal(2, found.Count);
            Assert.Equal(new[] { "a.b", "d" }, found[0].Key);
            Assert.Equal(new[] { "_mcvs", "gameProfile" }, found[1].Key);
        }

        // ------------------------------------------------------------------ JsonSpanScanner

        [Theory]
        [InlineData("{\"a\":{\"b\":[1,{\"c\":\"}]\"}]}}", new[] { "a", "b" }, "[1,{\"c\":\"}]\"}]")]
        [InlineData("  {\"a\" : 1.50e+3 , \"b\":2}", new[] { "a" }, "1.50e+3")]
        [InlineData("{\"x\\\"y\":true}", new[] { "x\"y" }, "true")]
        [InlineData("{\"\\u00e9\":null}", new[] { "é" }, "null")]
        [InlineData("{\"a\":1,\"a\":{\"z\":2}}", new[] { "a" }, "{\"z\":2}")]
        [InlineData("\uFEFF{\"s\":\"\\\\\"}", new[] { "s" }, "\"\\\\\"")]
        [InlineData("{\"p\":{\"q\":{}}}", new[] { "p", "q" }, "{}")]
        public void ScannerFindsTheExactValueSpan(string json, string[] path, string expected)
        {
            Assert.True(JsonSpanScanner.TryFindValue(json, path, out int start, out int length));
            Assert.Equal(expected, json.Substring(start, length));
        }

        [Theory]
        [InlineData("{\"a\":1}", new[] { "b" })]
        [InlineData("{\"a\":1}", new[] { "a", "b" })]
        [InlineData("[{\"a\":1}]", new[] { "a" })]
        [InlineData("{\"a\":{\"b\":1}", new[] { "a", "b" })]
        [InlineData("{\"a\":\"unterminated}", new[] { "a" })]
        [InlineData("", new[] { "a" })]
        public void ScannerFailsCleanlyOnMissingOrMalformedInput(string json, string[] path)
        {
            Assert.False(JsonSpanScanner.TryFindValue(json, path, out _, out _));
        }
    }
}
