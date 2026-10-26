using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Server.Services.Admin.Saves
{
    /// <summary>
    /// An applied operation waiting for the game to load its result (design §6.5), in
    /// &lt;data&gt;/admin/staged/probation.json:
    /// <code>
    /// {"id", "kind", "plan", "target", "appliedAt", "startsWithoutSave": 1, "lastStartAt", "preBackupId",
    ///  "relink": {"credential", "from", "to"} | null, "summary": {...}}
    /// </code>
    /// Cleared by the TS's first save of the target (every TS login saves the profile right after loading it,
    /// <c>GameServerComponent.TakeProfileLock</c> → <c>SaveProfile</c>); counted at every start; reverted at the third start
    /// without such a save (the apply start and two more), before the port opens. A start less than 10 s after the previous
    /// one is not counted (the double start of one restart, §8.8; a real start takes 10–20 s before the TS loads the profile).
    /// Only an operation after which the phone's login opens the target gets one (a replace, a switch or relink that moved
    /// the login; <see cref="StagedOperations.NeedsProbation"/>): a save of an account the phone does not load never comes.
    /// </summary>
    internal sealed class Probation
    {
        public const int RevertAtStarts = 3;
        public static readonly TimeSpan DoubleStartWindow = TimeSpan.FromSeconds(10);

        public const string FileName = "probation.json";

        private readonly string path;

        public Probation(string stagedFolder)
        {
            path = System.IO.Path.Combine(stagedFolder, FileName);
        }

        public string FilePath
        {
            get { return path; }
        }

        /// <summary>The probation, or null (none, or an unreadable file, which is removed).</summary>
        public Dictionary<string, object> Load()
        {
            if (!AdminFiles.IsPlainFile(path)) return null;
            try
            {
                Dictionary<string, object> o = MiniJson.ParseObject(AtomicFile.ReadAllTextOrNull(path));
                if (StagedOperation.IsId(JsonValue.GetString(o, "id")) && SaveBundle.IsAccountId(JsonValue.GetString(o, "target"))) return o;
            }
            catch (Exception)
            {
                // fall through: a damaged probation file must not block every start
            }
            AdminFiles.DeleteQuietly(path);
            return null;
        }

        public void Save(Dictionary<string, object> probation)
        {
            AtomicFile.WriteAllText(path, MiniJson.Serialize(probation, true) + "\n");
        }

        public void Delete()
        {
            AdminFiles.DeleteQuietly(path);
        }

        /// <summary>A new probation for an applied operation: one start (the apply start) without a save.</summary>
        public static Dictionary<string, object> For(StagedOperation op, bool relinked, DateTime nowUtc)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["id"] = op.Id;
            o["kind"] = op.Kind;
            o["plan"] = op.Plan;
            o["target"] = op.Target;
            o["appliedAt"] = TimeFormat.Iso8601Millis(nowUtc);
            o["startsWithoutSave"] = 1L;
            o["lastStartAt"] = TimeFormat.Iso8601Millis(nowUtc);
            o["preBackupId"] = op.PreBackupId;
            o["relink"] = relinked && op.Relink != null ? op.Relink.ToJson(false) : null;
            o["summary"] = op.SummaryJson();
            return o;
        }

        /// <summary>
        /// Counts one start (unless it follows the previous one within 10 s) and saves the probation. True when it is due
        /// for the automatic revert.
        /// </summary>
        public static bool CountStart(Dictionary<string, object> probation, DateTime nowUtc)
        {
            DateTime last;
            long starts = JsonValue.GetLong(probation, "startsWithoutSave", 1);
            if (TimeFormat.TryParse(JsonValue.GetString(probation, "lastStartAt"), out last) && nowUtc - last < DoubleStartWindow && nowUtc >= last)
                return starts >= RevertAtStarts;
            starts++;
            probation["startsWithoutSave"] = starts;
            probation["lastStartAt"] = TimeFormat.Iso8601Millis(nowUtc);
            return starts >= RevertAtStarts;
        }

        /// <summary>The probation as the page sees it (credential masked).</summary>
        public static Dictionary<string, object> ForPage(Dictionary<string, object> probation)
        {
            if (probation == null) return null;
            var o = JsonValue.DeepCloneObject(probation);
            var relink = JsonValue.GetObject(o, "relink");
            if (relink != null) relink["credential"] = AdminApi.MaskCredential(JsonValue.GetString(relink, "credential"));
            return o;
        }
    }
}
