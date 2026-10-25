using System.Collections.Generic;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Storage;
using Xunit;

namespace McvsOffline.Tests.Storage
{
    /// <summary>The stored file format and the Seshat write operations, without I/O.</summary>
    public class ProfileFileAndOperationsTests
    {
        private static ProfileDocument Doc(string rootJson = "{}")
        {
            var doc = new ProfileDocument("acc");
            doc.Root = MiniJson.ParseObject(rootJson);
            return doc;
        }

        private static WriteOutcome Apply(ProfileDocument doc, string selector, string objectText, string operation = null, string visibility = null)
        {
            return SeshatOperations.Apply(doc, new StorageWrite { Selector = selector, ObjectText = objectText, Operation = operation, Visibility = visibility });
        }

        // ------------------------------------------------------------------ file format

        [Fact]
        public void FileRoundTripKeepsVerbatimTextAndMetadata()
        {
            var doc = Doc("{\"level\":3}");
            doc.Revision = 12;
            doc.Created = "2026-10-01T00:00:00Z";
            doc.Modified = "2026-10-25T10:00:00Z";
            doc.Visibility["*"] = "restricted";
            Apply(doc, "_mcvs.gameProfile", GameData.GameProfileDump);
            Apply(doc, "_mcvs.currentMatch", "{ \"spaced\" : [ 1 , 2 ] }");
            Apply(doc, "note", "not json");

            string text = ProfileFile.Serialize(doc);
            Assert.Contains(GameData.GameProfileDump, text); // inline, readable, unchanged
            var warnings = new List<string>();
            ProfileDocument back = ProfileFile.Parse(text, "acc", warnings);
            Assert.Empty(warnings);
            Assert.True(back.IsStored);
            Assert.Equal(12, back.Revision);
            Assert.Equal("2026-10-01T00:00:00Z", back.Created);
            Assert.Equal("restricted", back.Visibility["*"]);

            ProfileJson.TryGet(back.Root, new[] { "_mcvs", "gameProfile" }, out var dump);
            Assert.Equal(GameData.GameProfileDump, Assert.IsType<VerbatimJson>(dump).Text);
            ProfileJson.TryGet(back.Root, new[] { "_mcvs", "currentMatch" }, out var match);
            Assert.Equal("{ \"spaced\" : [ 1 , 2 ] }", ((VerbatimJson)match).Text);
            var note = Assert.IsType<VerbatimJson>(back.Root["note"]);
            Assert.False(note.IsJson);
            Assert.Equal("not json", note.Text);
            Assert.Equal(text, ProfileFile.Serialize(back)); // stable: re-saving changes nothing
        }

        [Fact]
        public void HandEditedFilesStillLoad()
        {
            var doc = Doc();
            Apply(doc, "_mcvs.gameProfile", "{\"a\":1,\"b\":\"x\"}");
            string text = ProfileFile.Serialize(doc);

            // Reformatted by an editor: the verbatim value is recovered from the new text.
            string reformatted = text.Replace("{\"a\":1,\"b\":\"x\"}", "{\n      \"a\": 1,\n      \"b\": \"x\"\n    }");
            var warnings = new List<string>();
            var back = ProfileFile.Parse(reformatted, "acc", warnings);
            Assert.Empty(warnings);
            ProfileJson.TryGet(back.Root, new[] { "_mcvs", "gameProfile" }, out var node);
            Assert.Equal("{\n      \"a\": 1,\n      \"b\": \"x\"\n    }", ((VerbatimJson)node).Text);

            // The listed value was edited away: a warning, and the value is served re-serialized.
            string removed = text.Replace("\"gameProfile\": {\"a\":1,\"b\":\"x\"}", "\"other\": 1");
            warnings.Clear();
            back = ProfileFile.Parse(removed, "acc", warnings);
            Assert.Single(warnings);
            Assert.Equal(1L, JsonPath.Get(back.Root, "_mcvs.other"));
        }

        [Fact]
        public void PlainDocumentsAreImported()
        {
            var warnings = new List<string>();
            var doc = ProfileFile.Parse("{\"level\":5,\"_mcvs\":{\"gameProfile\":{\"@version\":\"0.1\"}}}", "acc", warnings);
            Assert.True(doc.IsStored);
            Assert.Equal(5L, doc.Root["level"]);
            Assert.Single(warnings);
        }

        [Theory]
        [InlineData("")]
        [InlineData("{\"format\":\"mcvs-offline/seshat-profile\",\"profile\":")]
        [InlineData("[1,2]")]
        [InlineData("{\"format\":\"mcvs-offline/seshat-profile\",\"profile\":[]}")]
        public void UnreadableFilesThrowInsteadOfLookingEmpty(string text)
        {
            Assert.Throws<ProfileFileException>(() => ProfileFile.Parse(text, "acc", null));
        }

        // ------------------------------------------------------------------ operations

        [Fact]
        public void SetStoresVerbatimAndCreatesIntermediates()
        {
            var doc = Doc("{\"a\":5}");
            var outcome = Apply(doc, "a.b.c", "{\"x\":1.0}");
            Assert.True(outcome.Changed);
            Assert.Equal("{\"a\":{\"b\":{\"c\":{\"x\":1.0}}}}", ProfileJson.Serialize(doc.Root));
            Assert.IsType<VerbatimJson>(JsonPath.Get(doc.Root, "a.b.c"));

            // Same text again: nothing to save.
            Assert.False(Apply(doc, "a.b.c", "{\"x\":1.0}").Changed);
            Assert.True(Apply(doc, "a.b.c", "{\"x\":1}").Changed);
        }

        [Fact]
        public void RootSetReplacesTheDocumentAndAsksForABackup()
        {
            var doc = Doc("{\"a\":1,\"b\":2}");
            var outcome = Apply(doc, "", "{\"c\":3,\"credential\":\"x\"}");
            Assert.True(outcome.Changed);
            Assert.Equal("replace", outcome.BackupReason);
            Assert.Equal("{\"c\":3}", ProfileJson.Serialize(doc.Root));
            Assert.NotNull(Apply(doc, "", "[1]").Error);
            Assert.True(Apply(doc, "", "").DeletesDocument);
        }

        [Fact]
        public void MergeIntoVerbatimSectionsAndScalars()
        {
            var doc = Doc();
            Apply(doc, "_mcvs.snapshot", "{\"a\":1,\"n\":{\"x\":1}}");
            Assert.True(Apply(doc, "_mcvs.snapshot", "{\"n\":{\"y\":2},\"b\":2}", "merge").Changed);
            Assert.Equal("{\"_mcvs\":{\"snapshot\":{\"a\":1,\"n\":{\"x\":1,\"y\":2},\"b\":2}}}", ProfileJson.Serialize(doc.Root));

            // Merging an object onto a scalar replaces it; merging a scalar sets it.
            Apply(doc, "s", "3");
            Apply(doc, "s", "{\"k\":1}", "merge");
            Apply(doc, "t", "\"v\"", "merge");
            Assert.Equal("{\"k\":1}", ProfileJson.Serialize(doc.Root["s"]));
            Assert.Equal("v", doc.Root["t"]);

            Assert.False(Apply(doc, "", "", "merge").Changed);
            Assert.NotNull(Apply(doc, "", "[]", "merge").Error);
            Assert.NotNull(Apply(doc, "", "nope", "merge").Error);
        }

        [Fact]
        public void ReadOnlyFieldsCannotBeWritten()
        {
            var doc = Doc();
            var outcome = Apply(doc, "credential", "\"anonymous:x\"");
            Assert.False(outcome.Changed);
            Assert.Contains(outcome.Notes, n => n.Contains("read-only"));
            Apply(doc, "", "{\"modified\":\"x\",\"last_session\":\"y\",\"level\":1}", "merge");
            Assert.Equal("{\"level\":1}", ProfileJson.Serialize(doc.Root));
        }

        [Fact]
        public void NullAtObjectSectionsIsDropped()
        {
            foreach (string section in new[] { "_mcvs.snapshot", "_mcvs.matchers", "inventory", "_inventory" })
            {
                var doc = Doc();
                Apply(doc, section, "{\"a\":1}");
                Apply(doc, section, "null");
                Assert.False(ProfileJson.Exists(doc.Root, section.Split('.')), section);
            }
            var other = Doc();
            Apply(other, "_mcvs.gameProfile", "null");
            Assert.True(ProfileJson.Exists(other.Root, new[] { "_mcvs", "gameProfile" }));
            Assert.False(SeshatOperations.HasGameProfile(other));
        }

        [Fact]
        public void AddNumbersKeepsIntegersAndFallsBackToDoubles()
        {
            Assert.Equal(5L, SeshatOperations.AddNumbers(2L, 3L));
            Assert.Equal(2.5, SeshatOperations.AddNumbers(2L, 0.5));
            Assert.Equal(3L, SeshatOperations.AddNumbers("x", 3L));
            Assert.Equal(9.2233720368547758E18, SeshatOperations.AddNumbers(long.MaxValue, 1L));
        }

        [Fact]
        public void VisibilityIsRecordedPerSelector()
        {
            var doc = Doc();
            Apply(doc, "", "{\"a\":1}", "merge", "{\"*\":\"restricted\"}");
            Apply(doc, "_mcvs.gameProfile", "{}", null, "restricted");
            Assert.Equal("{\"*\":\"restricted\",\"_mcvs.gameProfile\":\"restricted\"}", MiniJson.Serialize(doc.Visibility));
            // Unchanged value but new visibility: still a change worth saving.
            Assert.True(Apply(doc, "_mcvs.gameProfile", "{}", null, "public").Changed);
        }

        [Fact]
        public void MalformedSelectorsAndUnknownOperationsChangeNothing()
        {
            var doc = Doc("{\"a\":1}");
            Assert.NotNull(Apply(doc, "a..b", "1").Error);
            var unknown = Apply(doc, "a", "2", "frobnicate");
            Assert.True(unknown.IsUnknownOperation);
            Assert.False(unknown.Changed);
            Assert.Equal("{\"a\":1}", ProfileJson.Serialize(doc.Root));
        }
    }
}
