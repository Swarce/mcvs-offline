using System;
using System.Collections.Generic;
using McvsOffline.Server.Core.Json;
using Xunit;

namespace McvsOffline.Tests
{
    /// <summary>Dotted paths, deep merge and include_fields projection with Seshat semantics (verify-C3 §7).</summary>
    public class JsonPathTests
    {
        private static Dictionary<string, object> Obj(string json) => MiniJson.ParseObject(json);

        private static string S(object value) => MiniJson.Serialize(value);

        [Fact]
        public void GetFollowsDottedPathsAndArrayIndexes()
        {
            var doc = Obj("{\"_mcvs\":{\"gameProfile\":{\"x\":1},\"list\":[{\"v\":\"a\"},{\"v\":\"b\"}]},\"level\":7}");
            Assert.Equal(1L, JsonPath.Get(doc, "_mcvs.gameProfile.x"));
            Assert.Equal("b", JsonPath.Get(doc, "_mcvs.list.1.v"));
            Assert.Same(doc, JsonPath.Get(doc, ""));
            Assert.False(JsonPath.TryGet(doc, "_mcvs.missing", out _));
            Assert.False(JsonPath.TryGet(doc, "level.deeper", out _));
            Assert.False(JsonPath.TryGet(doc, "_mcvs.list.5", out _));
        }

        [Fact]
        public void NullValuesAreFoundAsPresent()
        {
            var doc = Obj("{\"a\":null}");
            Assert.True(JsonPath.TryGet(doc, "a", out var value));
            Assert.Null(value);
        }

        [Fact]
        public void SetCreatesAndReplacesIntermediates()
        {
            var doc = Obj("{\"_mcvs\":5}");
            JsonPath.Set(doc, "_mcvs.snapshot.level", 3L);
            JsonPath.Set(doc, "inventory", Obj("{}"));
            Assert.Equal("{\"_mcvs\":{\"snapshot\":{\"level\":3}},\"inventory\":{}}", S(doc));
            Assert.Throws<ArgumentException>(() => JsonPath.Set(doc, "", 1L));
            Assert.Throws<ArgumentException>(() => JsonPath.Set(doc, "a..b", 1L));
        }

        [Fact]
        public void RemoveKeepsTheOrderOfLaterInserts()
        {
            var doc = Obj("{\"a\":1,\"b\":2,\"c\":3}");
            Assert.True(JsonPath.Remove(doc, "b"));
            doc["d"] = 4L;
            Assert.Equal("{\"a\":1,\"c\":3,\"d\":4}", S(doc));
            Assert.False(JsonPath.Remove(doc, "x.y"));
            var nested = Obj("{\"_mcvs\":{\"gameProfile\":{},\"currentMatch\":{}}}");
            Assert.True(JsonPath.Remove(nested, "_mcvs.gameProfile"));
            Assert.Equal("{\"_mcvs\":{\"currentMatch\":{}}}", S(nested));
        }

        [Fact]
        public void DeepMergeRecursesIntoObjectsAndReplacesEverythingElse()
        {
            // R5: a root merge carrying a partial _mcvs must keep gameProfile/currentMatch (verify-C3 §7.4).
            var target = Obj("{\"level\":1,\"_mcvs\":{\"gameProfile\":{\"keep\":true},\"snapshot\":{\"a\":1,\"b\":[1,2]},\"currentMatch\":{}},\"tags\":[1,2,3]}");
            var patch = Obj("{\"level\":2,\"country\":null,\"_mcvs\":{\"snapshot\":{\"b\":[9],\"c\":3},\"matchers\":{\"m\":1}},\"tags\":[]}");
            JsonPath.DeepMerge(target, patch);
            Assert.Equal("{\"level\":2,\"_mcvs\":{\"gameProfile\":{\"keep\":true},\"snapshot\":{\"a\":1,\"b\":[9],\"c\":3},\"currentMatch\":{},\"matchers\":{\"m\":1}},\"tags\":[],\"country\":null}", S(target));
        }

        [Fact]
        public void DeepMergeCopiesPatchValues()
        {
            var target = Obj("{}");
            var patch = Obj("{\"a\":{\"b\":[1]}}");
            JsonPath.DeepMerge(target, patch);
            ((List<object>)((Dictionary<string, object>)patch["a"])["b"]).Add(2L);
            Assert.Equal("{\"a\":{\"b\":[1]}}", S(target));
        }

        [Fact]
        public void ProjectReturnsNestedObjectsForDottedFields()
        {
            // R2: include_fields with dotted paths must come back nested; SelectToken would miss flat keys (verify-C3 §3.2).
            var doc = Obj("{\"_mcvs\":{\"gameProfile\":{\"big\":1},\"matchers\":{\"m\":1},\"snapshot\":{\"s\":2}},\"level\":5,\"credential\":\"fed_id:x\",\"inventory\":{\"Credits\":3}}");
            var fields = JsonPath.SplitFieldList("_mcvs.matchers, _mcvs.snapshot,level,xp,inventory,_inventory");
            var projected = JsonPath.Project(doc, fields);
            Assert.Equal("{\"_mcvs\":{\"matchers\":{\"m\":1},\"snapshot\":{\"s\":2}},\"level\":5,\"inventory\":{\"Credits\":3}}", S(projected));
        }

        [Fact]
        public void ProjectHandlesOverlappingAndInvalidPaths()
        {
            var doc = Obj("{\"a\":{\"b\":1,\"c\":2},\"x.y\":3}");
            Assert.Equal("{\"a\":{\"b\":1,\"c\":2}}", S(JsonPath.Project(doc, new[] { "a.b", "a" })));
            Assert.Equal("{\"a\":{\"b\":1,\"c\":2}}", S(JsonPath.Project(doc, new[] { "a", "a.b" })));
            // Stored keys with dots are opaque, never addressable (gameProfile has keys like "Attachments.'0'").
            Assert.Equal("{}", S(JsonPath.Project(doc, new[] { "x.y", "a..b", "" })));
        }

        [Fact]
        public void ProjectDoesNotAliasTheSource()
        {
            var doc = Obj("{\"a\":{\"b\":[1]}}");
            var p = JsonPath.Project(doc, new[] { "a" });
            ((List<object>)((Dictionary<string, object>)p["a"])["b"]).Add(2L);
            Assert.Equal("{\"a\":{\"b\":[1]}}", S(doc));
        }
    }
}
