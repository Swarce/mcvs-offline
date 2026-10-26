using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin.Saves;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>Server-side collection of transfer parts across pastes and the kept uploads (design §4.6, §6.2).</summary>
    public class ImportUploadsTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);

        private static (ImportUploads Uploads, ManualClock Clock) Create(TempDir dir)
        {
            var clock = new ManualClock(T0);
            Directory.CreateDirectory(dir.Combine("uploads"));
            return (new ImportUploads(dir.Combine("uploads"), clock, null), clock);
        }

        [Fact]
        public void PartsCollectedOverSeveralPastesInAnyOrder()
        {
            using var dir = new TempDir();
            var (uploads, _) = Create(dir);
            byte[] bundle = SaveTransferTests.SampleBundle();
            TransferExport export = SaveTransfer.Encode(bundle, 3900, "1a2b3c4d", true);
            int n = export.Parts.Count;
            Assert.True(n >= 4);

            PasteOutcome first = uploads.AddPaste("Player: " + export.Parts[0] + " thanks!");
            Assert.False(first.Complete);
            Assert.Equal("1a2b3c4d", first.Set);
            Assert.Equal(n, first.Total);
            Assert.Equal(new[] { 1 }, first.Have);
            Assert.Equal(Enumerable.Range(2, n - 1), first.Missing);
            Assert.Equal("Got part 1 of " + n + ". Paste parts 2 to " + n + ".", first.Message);

            PasteOutcome again = uploads.AddPaste(export.Parts[0]);   // the same part twice changes nothing
            Assert.Equal(new[] { 1 }, again.Have);

            PasteOutcome third = uploads.AddPaste(export.Parts[2]);
            Assert.Equal("Got parts 1 and 3 of " + n + ". Paste parts 2 and 4 to " + n + ".", third.Message);

            var rest = Enumerable.Range(1, n - 1).Where(i => i != 2).Select(i => export.Parts[i]).Reverse();
            PasteOutcome done = uploads.AddPaste(string.Join("\r\n\r\n", rest));
            Assert.True(done.Complete);
            Assert.Equal(Encoding.UTF8.GetString(bundle), done.BundleText);
        }

        [Fact]
        public void DamagedAndForeignPartsAreReportedNotStored()
        {
            using var dir = new TempDir();
            var (uploads, _) = Create(dir);
            TransferExport export = SaveTransfer.Encode(SaveTransferTests.SampleBundle(), 3900, "55556666", true);
            TransferExport other = SaveTransfer.Encode(SaveTransferTests.SampleBundle(40), 3900, "55556666", true); // same set id, another save
            string damaged = export.Parts[1].Replace("\n~", "") + " and some chat";

            PasteOutcome outcome = uploads.AddPaste(export.Parts[0] + "\n" + damaged);
            Assert.Equal(new[] { 1 }, outcome.Have);
            Assert.Contains(outcome.Rejected, r => r.Part == 2 && r.Reason == SaveTransfer.Damaged);
            Assert.Contains("Part 2 was not used: damaged", outcome.Message);

            PasteOutcome foreign = uploads.AddPaste(other.Parts[1]);
            Assert.Equal(new[] { 1 }, foreign.Have);
            var rejection = Assert.Single(foreign.Rejected);
            Assert.Equal(2, rejection.Part);
            Assert.Equal(SaveTransfer.OtherExport, rejection.Reason);
        }

        [Fact]
        public void AWholeBundleTextNeedsNoParts()
        {
            using var dir = new TempDir();
            var (uploads, _) = Create(dir);
            PasteOutcome whole = uploads.AddPaste("  {\"format\": \"mcvs-offline/save-bundle\"}\n");
            Assert.True(whole.Complete);
            Assert.Equal("{\"format\": \"mcvs-offline/save-bundle\"}", whole.BundleText);

            PasteOutcome nothing = uploads.AddPaste("hello there");
            Assert.True(nothing.NothingFound);
            Assert.False(nothing.Complete);
        }

        [Fact]
        public void AnInflateBombIsTooLargeAndAMismatchIsUndecodable()
        {
            using var dir = new TempDir();
            var (uploads, _) = Create(dir);
            TransferExport bomb = SaveTransfer.Encode(new byte[SaveTransfer.MaxInflatedBytes + 1], 60000, "77778888", true);
            PasteOutcome outcome = uploads.AddPaste(string.Concat(bomb.Parts));
            Assert.True(outcome.Complete);
            Assert.True(outcome.TooLarge);
            Assert.Null(outcome.BundleText);
        }

        [Fact]
        public void SetsAndUploadsExpireAfterAnHourAndOnlyTheNewestAreKept()
        {
            using var dir = new TempDir();
            var (uploads, clock) = Create(dir);
            var sets = new[] { "a0000001", "a0000002", "a0000003", "a0000004" };
            foreach (string set in sets)
            {
                TransferExport e = SaveTransfer.Encode(SaveTransferTests.SampleBundle(), 3900, set, true);
                uploads.AddPaste(e.Parts[0]);
                clock.Advance(TimeSpan.FromMinutes(1));
            }
            uploads.AddPaste("MCVS-SAVE:bad");   // a paste prunes first
            var left = Directory.GetDirectories(uploads.Folder).Select(Path.GetFileName).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "set-a0000002", "set-a0000003", "set-a0000004" }, left);

            var ids = Enumerable.Range(0, 5).Select(i => { clock.Advance(TimeSpan.FromSeconds(1)); return uploads.Save("{\"n\":" + i + "}", "paste", null); }).ToList();
            Assert.All(ids, id => Assert.Matches("^u-[0-9a-f]{16}$", id));
            Assert.Null(uploads.Load(ids[0]));
            Assert.Null(uploads.Load(ids[1]));
            Assert.Equal("{\"n\":4}", uploads.Load(ids[4]).Text);
            Assert.Equal("paste", uploads.Load(ids[4]).SourceType);

            clock.Advance(TimeSpan.FromMinutes(61));
            Assert.Null(uploads.Load(ids[4]));
            uploads.AddPaste("MCVS-SAVE:bad");
            Assert.Empty(Directory.GetDirectories(uploads.Folder));
        }

        [Theory]
        [InlineData("../accounts")]
        [InlineData("u-0123")]
        [InlineData("u-0123456789ABCDEF")]
        [InlineData("u-0123456789abcdef/../x")]
        [InlineData("")]
        [InlineData(null)]
        public void MalformedUploadIdsAreNeverLookedUp(string id)
        {
            using var dir = new TempDir();
            var (uploads, _) = Create(dir);
            Assert.Null(uploads.Load(id));
        }

        [Fact]
        public void AFileMustHoldOneWholeExport()
        {
            byte[] bundle = SaveTransferTests.SampleBundle();
            TransferExport export = SaveTransfer.Encode(bundle, 60000, "f0f0f0f0", true);
            string file = SaveTransfer.FileText(export, "header");
            PasteOutcome complete = ImportUploads.FromText(file);
            Assert.True(complete.Complete);
            Assert.Equal(Encoding.UTF8.GetString(bundle), complete.BundleText);

            TransferExport small = SaveTransfer.Encode(bundle, 3900, "f1f1f1f1", true);
            PasteOutcome partial = ImportUploads.FromText(small.Parts[0] + small.Parts[2]);
            Assert.False(partial.Complete);
            Assert.Equal(new[] { 1, 3 }, partial.Have);
            Assert.Contains(2, partial.Missing);
        }

        [Theory]
        [InlineData(new[] { 1 }, "1")]
        [InlineData(new[] { 1, 3 }, "1 and 3")]
        [InlineData(new[] { 1, 2 }, "1 and 2")]
        [InlineData(new[] { 1, 3, 5 }, "1, 3 and 5")]
        [InlineData(new[] { 2, 3, 4, 5 }, "2 to 5")]
        [InlineData(new[] { 1, 2, 3, 5 }, "1 to 3 and 5")]
        [InlineData(new[] { 1, 2, 4 }, "1, 2 and 4")]
        public void PartListsReadNaturally(int[] numbers, string text)
        {
            Assert.Equal(text, ImportUploads.ListText(numbers.ToList()));
        }
    }
}
