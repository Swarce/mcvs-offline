using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Admin.Saves;
using McvsOffline.Server.Services.Storage;
using McvsOffline.Tests.Storage;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>The save bundle (design §6.1): byte-for-byte export, strict validation, raw profile files.</summary>
    public class SaveBundleTests
    {
        private const string Account = "0123456789abcdef01234567";
        private static readonly DateTime Now = new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);

        /// <summary>A profile file as the store writes it, with a verbatim game profile (odd spacing and escapes).</summary>
        internal static string ProfileFileText(string account, string dump = GameData.GameProfileDump, long level = 41, long revision = 900)
        {
            var doc = new ProfileDocument(account) { Revision = revision, Created = "2026-01-02T03:04:05Z", Modified = "2026-10-26T09:00:00Z", IsStored = true };
            doc.Root["level"] = level;
            var mcvs = ProfileJson.NewObject();
            if (dump != null) mcvs["gameProfile"] = VerbatimJson.FromValidJson(dump);
            doc.Root["_mcvs"] = mcvs;
            return ProfileFile.Serialize(doc);
        }

        internal static string BundleText(string profileText, string account = Account, Action<Dictionary<string, object>> edit = null,
                                          Dictionary<string, string> messages = null)
        {
            var bundle = new SaveBundle
            {
                AccountId = account, Alias = "123456789", AccountCreated = "2026-01-02T03:04:05Z", Created = "2026-10-26T09:30:00.000Z",
                ServerVersion = "0.1.0-dev", ServerBuild = "9", ServerInstance = "mcvs1a2b3c4d", ProfileText = profileText,
            };
            if (messages != null) foreach (var kv in messages) bundle.Messages[kv.Key] = kv.Value;
            string json = bundle.ToJson();
            if (edit == null) return json;
            var o = MiniJson.ParseObject(json);
            edit(o);
            return MiniJson.Serialize(o);
        }

        [Fact]
        public void ARoundTripKeepsTheProfileFileByteForByte()
        {
            string dump = "{ \"@version\" : \"0.1\",\"$default\":{\"/Profile\":{\"Name\":\"\\u00e9\\/x\",\"Big\":1.50e+3 , \"Raw\":\"é漢\\t\"}}}";
            string file = ProfileFileText(Account, dump);
            var messages = new Dictionary<string, string> { ["inbox"] = "{\"m\":[{\"id\":\"1\"}]}", ["secured"] = "{\"m\":[]}" };
            string text = BundleText(file, messages: messages);

            SaveBundle parsed = SaveBundle.Parse(text, Now);
            Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
            Assert.Equal(file, parsed.ProfileText);
            Assert.Equal(Encoding.UTF8.GetBytes(file), Encoding.UTF8.GetBytes(parsed.ProfileText));
            Assert.Equal(Account, parsed.AccountId);
            Assert.Equal("123456789", parsed.Alias);
            Assert.True(parsed.HasGameProfile);
            Assert.Equal(41, parsed.Level);
            Assert.Equal(900, parsed.Revision);
            Assert.Equal(messages, parsed.Messages);
            object node;
            Assert.True(ProfileJson.TryGet(parsed.Document.Root, SeshatOperations.GameProfilePath, out node));
            Assert.Equal(dump, ProfileJson.ResponseText(node));

            var o = MiniJson.ParseObject(text);
            Assert.Equal((long)Encoding.UTF8.GetByteCount(file), o["profileLength"]);
            Assert.Equal(SaveBundle.Crc32Hex(Encoding.UTF8.GetBytes(file)), o["profileCrc32"]);
            Assert.Equal(8, ((string)o["profileCrc32"]).Length);
        }

        [Fact]
        public void BuildExportsTheStoredFileAndTheTwoMailboxes()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Path);
            var context = server.Context;
            var store = context.Features.Get<LocalProfileStore>();
            var account = context.Accounts.GetOrCreate("anonymous:bundle-build", "dev");
            context.Accounts.Update(account.Id, a => a.Data["alias"] = "987654321");
            store.Commit(account.Id, "test", "", "http", doc =>
            {
                doc.Root["_mcvs"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["gameProfile"] = VerbatimJson.FromValidJson(GameData.GameProfileDump) };
                return new WriteOutcome { Changed = true };
            });
            var messages = context.OpenStore("messages");
            messages.WriteText(account.Id + ".inbox", "{\"x\":1}");
            messages.WriteText(account.Id + ".gcm", "{\"push\":1}");
            context.Settings.Set("superPassword", "secret-value");

            SaveBundle bundle = SaveBundle.Build(context, store, account.Id, true);
            Assert.Equal(File.ReadAllText(store.PathFor(account.Id)), bundle.ProfileText);
            Assert.Equal("987654321", bundle.Alias);
            Assert.Equal(new[] { "inbox" }, bundle.Messages.Keys);
            Assert.False(bundle.Settings.ContainsKey("superPassword"));
            string json = bundle.ToJson();
            Assert.DoesNotContain("secret-value", json);
            Assert.DoesNotContain("anonymous:bundle-build", json);   // no credential in an export

            SaveBundle parsed = SaveBundle.Parse(json, Now);
            Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
            Assert.Equal(bundle.ProfileText, parsed.ProfileText);
            Assert.Null(SaveBundle.Build(context, store, "fedcba9876543210fedcba98", false));
        }

        [Fact]
        public void ARawProfileFileIsAcceptedAsABundleWithoutMessages()
        {
            string file = ProfileFileText(Account);
            SaveBundle parsed = SaveBundle.Parse(file, Now);
            Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
            Assert.True(parsed.IsRawProfile);
            Assert.Equal(Account, parsed.AccountId);
            Assert.Empty(parsed.Messages);
            Assert.Equal(file, parsed.ProfileText);
        }

        public static IEnumerable<object[]> Broken()
        {
            string good = ProfileFileText(Account);
            yield return new object[] { "{}", SaveBundle.ErrorNotASave };
            yield return new object[] { "not json at all", SaveBundle.ErrorNotASave };
            yield return new object[] { "[1,2]", SaveBundle.ErrorNotASave };
            yield return new object[] { "{\"port\": 51780, \"superPassword\": \"x\", \"economy\": {}}", SaveBundle.ErrorNotASave };   // a settings.json
            yield return new object[] { "{\"@version\":\"0.1\",\"level\":3}", SaveBundle.ErrorNotASave };                          // no marker
            yield return new object[] { BundleText(good, edit: o => o["version"] = 2L), SaveBundle.ErrorNotASave };
            yield return new object[] { BundleText(good, edit: o => o["version"] = 1.0), SaveBundle.ErrorNotASave };
            yield return new object[] { BundleText(good.Replace(Account, Account.ToUpperInvariant()), Account.ToUpperInvariant()), SaveBundle.ErrorAccount };
            yield return new object[] { BundleText(good, "0123"), SaveBundle.ErrorAccount };
            yield return new object[] { BundleText(good, "fedcba9876543210fedcba98"), SaveBundle.ErrorProfile };                   // account mismatch
            yield return new object[] { BundleText(good, edit: o => o["profileCrc32"] = "00000000"), SaveBundle.ErrorIncomplete };
            yield return new object[] { BundleText(good, edit: o => o["profileLength"] = 12L), SaveBundle.ErrorIncomplete };
            yield return new object[] { BundleText(good, edit: o => o["profile"] = ((string)o["profile"]).Substring(0, 200)), SaveBundle.ErrorIncomplete };
            yield return new object[] { BundleText("{\"@version\":\"0.1\",\"level\":3}"), SaveBundle.ErrorProfile };               // a plain document
            yield return new object[] { BundleText(good.Replace("\"version\": 1", "\"version\": 2")), SaveBundle.ErrorProfile };
            yield return new object[] { BundleText(ProfileFileText(Account, null)), SaveBundle.ErrorNoProgress };
            yield return new object[] { BundleText(ProfileFileText(Account, "{}")), SaveBundle.ErrorNoProgress };
            yield return new object[] { BundleText(good, edit: o => o["messages"] = new Dictionary<string, object> { ["gcm"] = "{}" }), SaveBundle.ErrorInbox };
            yield return new object[] { BundleText(good, edit: o => o["messages"] = new Dictionary<string, object> { ["inbox"] = 5L }), SaveBundle.ErrorInbox };
            yield return new object[] { BundleText(good, edit: o => o["messages"] = "inbox"), SaveBundle.ErrorInbox };
            yield return new object[] { BundleText(good, messages: new Dictionary<string, string> { ["inbox"] = "[1]" }), SaveBundle.ErrorInbox };
            yield return new object[] { BundleText(good, messages: new Dictionary<string, string> { ["inbox"] = "{broken" }), SaveBundle.ErrorInbox };
            yield return new object[] { ProfileFileText(Account, null), SaveBundle.ErrorNoProgress };                               // raw, no progress
        }

        [Theory]
        [MemberData(nameof(Broken))]
        public void EveryStrictCheckIsAnError(string text, string error)
        {
            SaveBundle parsed = SaveBundle.Parse(text, Now);
            Assert.False(parsed.IsValid);
            Assert.Contains(error, parsed.Errors);
        }

        [Fact]
        public void ParseWarningsAndAFutureDateAreWarningsOnly()
        {
            string file = ProfileFileText(Account);
            string text = BundleText(file, edit: o => o["created"] = "2027-01-01T00:00:00.000Z");
            SaveBundle parsed = SaveBundle.Parse(text, Now);
            Assert.True(parsed.IsValid);
            Assert.Contains(parsed.Warnings, w => w.Contains("future"));
        }

        [Fact]
        public void ProfileProblemsChecksABackupFile()
        {
            ProfileDocument doc;
            Assert.Empty(SaveBundle.ProfileProblems(ProfileFileText(Account), Account, true, null, out doc));
            Assert.NotNull(doc);
            Assert.Equal(new[] { SaveBundle.ErrorProfile }, SaveBundle.ProfileProblems(ProfileFileText(Account), "fedcba9876543210fedcba98", true, null, out doc));
            Assert.Equal(new[] { SaveBundle.ErrorNoProgress }, SaveBundle.ProfileProblems(ProfileFileText(Account, null), Account, true, null, out doc));
            Assert.Empty(SaveBundle.ProfileProblems(ProfileFileText(Account, null), Account, false, null, out doc));
        }
    }
}
