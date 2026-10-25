using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Storage
{
    /// <summary>
    /// A profile write as the game sends it: <c>POST profiles/{cred}/myprofile[/{selector}]</c> with
    /// <c>object=</c>, optional <c>operation=</c> and <c>visibility=</c> (verify-C3 §2.2).
    /// </summary>
    public sealed class StorageWrite
    {
        /// <summary>Dotted selector; "" or null is the document root.</summary>
        public string Selector { get; set; }

        /// <summary>Lower-case operation; null or "" means set (the client omits operation=set).</summary>
        public string Operation { get; set; }

        /// <summary>The object= value exactly as received; null when the parameter is absent.</summary>
        public string ObjectText { get; set; }

        /// <summary>The visibility= value as received (selector form "restricted", root form {"*":"restricted"}), or null.</summary>
        public string Visibility { get; set; }

        /// <summary>If-Match header, if any. Logged and ignored: we never hand out ETags (verify-C3 §3.5).</summary>
        public string IfMatch { get; set; }

        /// <summary>Normalized operation name ("set" when none was given).</summary>
        public string EffectiveOperation
        {
            get { return string.IsNullOrEmpty(Operation) ? SeshatOperations.Set : Operation.Trim().ToLowerInvariant(); }
        }
    }

    /// <summary>What applying a <see cref="StorageWrite"/> did.</summary>
    public sealed class WriteOutcome
    {
        public WriteOutcome()
        {
            Notes = new List<string>();
        }

        /// <summary>The document changed and must be saved.</summary>
        public bool Changed { get; set; }

        /// <summary>The whole document is to be deleted (root delete, or a root set with an empty object=).</summary>
        public bool DeletesDocument { get; set; }

        /// <summary>When set, the previous state is backed up before saving (destructive operations).</summary>
        public string BackupReason { get; set; }

        /// <summary>Non-null for a malformed request (answered 400; the game never sends these).</summary>
        public string Error { get; set; }

        public bool IsUnknownOperation { get; set; }

        /// <summary>Human-readable remarks for the log (ignored read-only fields, null sections dropped...).</summary>
        public List<string> Notes { get; private set; }

        public static WriteOutcome Fail(string error)
        {
            var o = new WriteOutcome();
            o.Error = error;
            return o;
        }
    }

    /// <summary>
    /// The Seshat document operations, applied in memory to a <see cref="ProfileDocument"/> (no I/O, no locking).
    /// Semantics (verify-C3 §7, which overrides report 03 §11):
    /// <list type="bullet">
    /// <item><b>set</b> (no operation=): the value replaces the one at the selector, intermediates are created. The
    /// value is stored verbatim. An empty <c>object=</c> deletes (it is how the client's DeleteProfileField looks).
    /// JSON null is stored, except at the section selectors the game reloads as objects
    /// (<see cref="ObjectSections"/>), where it deletes instead: "null" read back makes <c>SetProfile("null")</c>
    /// throw (verify-C3 §7.5).</item>
    /// <item><b>merge</b>: recursive object merge (the R5 save carries a partial <c>_mcvs</c> and must keep
    /// <c>gameProfile</c> and <c>currentMatch</c>); arrays and scalars are replaced; null is stored.</item>
    /// <item><b>delete</b>: removes the selector (the root: the whole document).</item>
    /// <item><b>add / increment / append / batch_set / batch_delete</b>: not used by the game; implemented with the
    /// obvious meaning so that a caller using them gets a sensible result.</item>
    /// </list>
    /// Read-only standard fields (<see cref="ReadOnlyFields"/>) are server-maintained: writes to them are ignored.
    /// </summary>
    public static class SeshatOperations
    {
        public const string Set = "set";
        public const string Merge = "merge";
        public const string Delete = "delete";
        public const string Add = "add";
        public const string Increment = "increment";
        public const string Append = "append";
        public const string BatchSet = "batch_set";
        public const string BatchDelete = "batch_delete";

        /// <summary>The standard-profile fields the server maintains (OP/StandardProfile.cs field list, readOnly: true).</summary>
        public static readonly string[] ReadOnlyFields =
        {
            "credential", "created", "modified", "datacenter", "last_session", "total_spent", "total_transactions",
            "last_purchase", "banned_from",
        };

        /// <summary>
        /// Section selectors the game loads with <c>SetProfile(text)</c> and therefore needs to be an object or absent,
        /// never JSON null (verify-C3 §7.5).
        /// </summary>
        public static readonly string[][] ObjectSections =
        {
            new[] { McvsConstants.GameProfileSelector, "snapshot" },
            new[] { McvsConstants.GameProfileSelector, "matchers" },
            new[] { "inventory" },
            new[] { "_inventory" },
        };

        /// <summary>Path of the game-profile dump, "_mcvs.gameProfile".</summary>
        public static readonly string[] GameProfilePath = { McvsConstants.GameProfileSelector, "gameProfile" };

        public static bool IsReadOnlyField(string key)
        {
            return Array.IndexOf(ReadOnlyFields, key) >= 0;
        }

        /// <summary>True when the document holds a non-empty game-profile dump.</summary>
        public static bool HasGameProfile(ProfileDocument doc)
        {
            object node;
            return doc != null && ProfileJson.TryGet(doc.Root, GameProfilePath, out node) && !ProfileJson.IsEmpty(node);
        }

        /// <summary>Applies <paramref name="write"/> to <paramref name="doc"/>.</summary>
        public static WriteOutcome Apply(ProfileDocument doc, StorageWrite write)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            if (write == null) throw new ArgumentNullException("write");
            string[] path;
            if (!ProfileJson.TryParsePath(write.Selector, out path))
                return WriteOutcome.Fail("malformed selector '" + write.Selector + "'");

            if (path.Length > 0 && IsReadOnlyField(path[0]))
            {
                var ignored = new WriteOutcome();
                ignored.Notes.Add("'" + path[0] + "' is a read-only field maintained by the server; write ignored");
                return ignored;
            }

            WriteOutcome outcome;
            string op = write.EffectiveOperation;
            switch (op)
            {
                case Set: outcome = ApplySet(doc, path, write.ObjectText); break;
                case Merge: outcome = ApplyMerge(doc, path, write.ObjectText); break;
                case Delete: outcome = ApplyDelete(doc, path); break;
                case Add:
                case Increment: outcome = ApplyAddOrIncrement(doc, path, write.ObjectText, op == Add); break;
                case Append: outcome = ApplyAppend(doc, path, write.ObjectText); break;
                case BatchSet: outcome = ApplyBatchSet(doc, path, write.ObjectText); break;
                case BatchDelete: outcome = ApplyBatchDelete(doc, path, write.ObjectText); break;
                default:
                    outcome = new WriteOutcome();
                    outcome.IsUnknownOperation = true;
                    outcome.Notes.Add("unknown operation '" + op + "'; nothing changed");
                    return outcome;
            }
            if (outcome.Error != null || outcome.DeletesDocument) return outcome;

            if (write.Visibility != null && RecordVisibility(doc, path, write.Visibility)) outcome.Changed = true;
            if (outcome.Changed) DropNullSections(doc, outcome);
            return outcome;
        }

        // ------------------------------------------------------------------ operations

        private static WriteOutcome ApplySet(ProfileDocument doc, string[] path, string objectText)
        {
            var outcome = new WriteOutcome();
            bool empty = string.IsNullOrEmpty(objectText);
            if (path.Length == 0)
            {
                if (empty)
                {
                    outcome.DeletesDocument = true;
                    outcome.BackupReason = "replace";
                    outcome.Notes.Add("root set with an empty object: document deleted");
                    return outcome;
                }
                object value;
                if (!MiniJson.TryParse(objectText, out value) || !(value is Dictionary<string, object>))
                    return WriteOutcome.Fail("a root set needs a JSON object");
                doc.Root = StripReadOnly((Dictionary<string, object>)value, outcome);
                outcome.Changed = true;
                outcome.BackupReason = "replace";
                return outcome;
            }

            if (empty)
            {
                outcome.Changed = ProfileJson.Remove(doc.Root, path);
                return outcome;
            }

            VerbatimJson verbatim = VerbatimJson.FromRequestText(objectText);
            if (!verbatim.IsJson)
                outcome.Notes.Add("object= is not valid JSON; stored as opaque text (" + objectText.Length.ToString(CultureInfo.InvariantCulture) + " chars)");
            if (verbatim.IsJson && verbatim.Value == null && IsObjectSection(path))
            {
                outcome.Changed = ProfileJson.Remove(doc.Root, path);
                outcome.Notes.Add("null for section '" + ProfileJson.FormatPath(path) + "' deleted instead of stored");
                return outcome;
            }

            object existing;
            if (ProfileJson.TryGet(doc.Root, path, out existing))
            {
                var old = existing as VerbatimJson;
                if (old != null && old.IsJson == verbatim.IsJson && string.Equals(old.Text, verbatim.Text, StringComparison.Ordinal))
                {
                    outcome.Notes.Add("value unchanged");
                    return outcome; // identical text: nothing to save
                }
            }
            ProfileJson.Set(doc.Root, path, verbatim);
            outcome.Changed = true;
            return outcome;
        }

        private static WriteOutcome ApplyMerge(ProfileDocument doc, string[] path, string objectText)
        {
            var outcome = new WriteOutcome();
            if (string.IsNullOrEmpty(objectText))
            {
                outcome.Notes.Add("merge with an empty object: nothing to do");
                return outcome;
            }
            object value;
            if (!MiniJson.TryParse(objectText, out value))
            {
                if (path.Length == 0) return WriteOutcome.Fail("a root merge needs a JSON object");
                outcome.Notes.Add("merge value is not valid JSON; stored as opaque text");
                ProfileJson.Set(doc.Root, path, VerbatimJson.FromOpaqueText(objectText));
                outcome.Changed = true;
                return outcome;
            }

            var patch = value as Dictionary<string, object>;
            if (path.Length == 0)
            {
                if (patch == null) return WriteOutcome.Fail("a root merge needs a JSON object");
                ProfileJson.DeepMerge(doc.Root, StripReadOnly(patch, outcome));
                outcome.Changed = true;
                return outcome;
            }

            Dictionary<string, object> target = patch == null ? null : GetObjectForWrite(doc.Root, path);
            if (target != null) ProfileJson.DeepMerge(target, patch);
            else ProfileJson.Set(doc.Root, path, value);
            outcome.Changed = true;
            return outcome;
        }

        private static WriteOutcome ApplyDelete(ProfileDocument doc, string[] path)
        {
            var outcome = new WriteOutcome();
            if (path.Length == 0)
            {
                outcome.DeletesDocument = true;
                outcome.BackupReason = "delete";
                return outcome;
            }
            outcome.Changed = ProfileJson.Remove(doc.Root, path);
            if (!outcome.Changed) outcome.Notes.Add("nothing to delete");
            return outcome;
        }

        private static WriteOutcome ApplyAddOrIncrement(ProfileDocument doc, string[] path, string objectText, bool isAdd)
        {
            var outcome = new WriteOutcome();
            object delta;
            if (string.IsNullOrEmpty(objectText) || !MiniJson.TryParse(objectText, out delta))
                return WriteOutcome.Fail("increment/add needs a JSON value");
            if (path.Length == 0)
            {
                var patch = delta as Dictionary<string, object>;
                if (patch == null) return WriteOutcome.Fail("a root increment/add needs a JSON object");
                foreach (var kv in StripReadOnly(patch, outcome)) AddInto(doc.Root, kv.Key, kv.Value, isAdd);
            }
            else
            {
                AddInto(EnsureParent(doc.Root, path), path[path.Length - 1], delta, isAdd);
            }
            outcome.Changed = true;
            return outcome;
        }

        private static WriteOutcome ApplyAppend(ProfileDocument doc, string[] path, string objectText)
        {
            var outcome = new WriteOutcome();
            object value;
            if (path.Length == 0) return WriteOutcome.Fail("append needs a selector");
            if (string.IsNullOrEmpty(objectText) || !MiniJson.TryParse(objectText, out value))
                return WriteOutcome.Fail("append needs a JSON value");
            var parent = EnsureParent(doc.Root, path);
            string key = path[path.Length - 1];
            object existing;
            var list = parent.TryGetValue(key, out existing) ? ProfileJson.Clone(ProfileJson.Unwrap(existing)) as List<object> : null;
            if (list == null) list = new List<object>();
            var items = value as List<object>;
            if (items != null) list.AddRange(items);
            else list.Add(value);
            parent[key] = list;
            outcome.Changed = true;
            return outcome;
        }

        private static WriteOutcome ApplyBatchSet(ProfileDocument doc, string[] path, string objectText)
        {
            var outcome = new WriteOutcome();
            object value;
            if (string.IsNullOrEmpty(objectText) || !MiniJson.TryParse(objectText, out value) || !(value is Dictionary<string, object>))
                return WriteOutcome.Fail("batch_set needs a JSON object of {path: value}");
            foreach (var kv in (Dictionary<string, object>)value)
            {
                string[] full;
                if (!TryCombine(path, kv.Key, out full) || full.Length == 0)
                {
                    outcome.Notes.Add("batch_set: skipped malformed path '" + kv.Key + "'");
                    continue;
                }
                if (IsReadOnlyField(full[0]))
                {
                    outcome.Notes.Add("batch_set: read-only field '" + full[0] + "' ignored");
                    continue;
                }
                ProfileJson.Set(doc.Root, full, kv.Value);
                outcome.Changed = true;
            }
            return outcome;
        }

        private static WriteOutcome ApplyBatchDelete(ProfileDocument doc, string[] path, string objectText)
        {
            var outcome = new WriteOutcome();
            var paths = new List<string>();
            object value;
            if (!string.IsNullOrEmpty(objectText) && MiniJson.TryParse(objectText, out value))
            {
                var arr = value as List<object>;
                var obj = value as Dictionary<string, object>;
                if (arr != null) foreach (var item in arr) { string s = item as string; if (s != null) paths.Add(s); }
                else if (obj != null) paths.AddRange(obj.Keys);
                else if (value is string) paths.Add((string)value);
            }
            else if (!string.IsNullOrEmpty(objectText))
            {
                paths.AddRange(JsonPath.SplitFieldList(objectText));
            }
            foreach (string p in paths)
            {
                string[] full;
                if (!TryCombine(path, p, out full) || full.Length == 0) continue;
                if (ProfileJson.Remove(doc.Root, full)) outcome.Changed = true;
            }
            return outcome;
        }

        // ------------------------------------------------------------------ helpers

        private static bool IsObjectSection(string[] path)
        {
            foreach (string[] section in ObjectSections)
            {
                if (SamePath(section, path)) return true;
            }
            return false;
        }

        private static bool SamePath(string[] a, string[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        /// <summary>Removes JSON null at the object sections (a merge may have put it there).</summary>
        private static void DropNullSections(ProfileDocument doc, WriteOutcome outcome)
        {
            foreach (string[] section in ObjectSections)
            {
                object node;
                if (ProfileJson.TryGet(doc.Root, section, out node) && ProfileJson.Unwrap(node) == null)
                {
                    ProfileJson.Remove(doc.Root, section);
                    outcome.Notes.Add("null for section '" + ProfileJson.FormatPath(section) + "' deleted instead of stored");
                }
            }
        }

        /// <summary>A copy of a root patch without the read-only fields (key order kept).</summary>
        private static Dictionary<string, object> StripReadOnly(Dictionary<string, object> patch, WriteOutcome outcome)
        {
            var result = ProfileJson.NewObject();
            foreach (var kv in patch)
            {
                if (IsReadOnlyField(kv.Key))
                {
                    outcome.Notes.Add("read-only field '" + kv.Key + "' ignored");
                    continue;
                }
                result[kv.Key] = kv.Value;
            }
            return result;
        }

        /// <summary>The mutable object at a path (materializing verbatim objects), or null if absent or not an object.</summary>
        private static Dictionary<string, object> GetObjectForWrite(Dictionary<string, object> root, string[] path)
        {
            Dictionary<string, object> current = root;
            foreach (string segment in path)
            {
                current = ProfileJson.MaterializeChild(current, segment);
                if (current == null) return null;
            }
            return current;
        }

        /// <summary>The mutable parent object of a path's last segment, creating intermediates like Set does.</summary>
        private static Dictionary<string, object> EnsureParent(Dictionary<string, object> root, string[] path)
        {
            Dictionary<string, object> current = root;
            for (int i = 0; i < path.Length - 1; i++)
            {
                Dictionary<string, object> child = ProfileJson.MaterializeChild(current, path[i]);
                if (child == null)
                {
                    child = ProfileJson.NewObject();
                    current[path[i]] = child;
                }
                current = child;
            }
            return current;
        }

        private static void AddInto(Dictionary<string, object> parent, string key, object delta, bool isAdd)
        {
            object existing;
            parent.TryGetValue(key, out existing);
            var deltaObj = delta as Dictionary<string, object>;
            if (deltaObj != null)
            {
                Dictionary<string, object> child = ProfileJson.MaterializeChild(parent, key);
                if (child == null)
                {
                    child = ProfileJson.NewObject();
                    parent[key] = child;
                }
                foreach (var kv in deltaObj) AddInto(child, kv.Key, kv.Value, isAdd);
                return;
            }
            object current = ProfileJson.Unwrap(existing);
            var list = current as List<object>;
            if (isAdd && list != null)
            {
                // add on an array: set-union of the new items
                var copy = (List<object>)ProfileJson.Clone(list);
                var items = delta as List<object> ?? new List<object> { delta };
                foreach (var item in items)
                {
                    bool present = false;
                    foreach (var have in copy) { if (ProfileJson.DeepEquals(have, item)) { present = true; break; } }
                    if (!present) copy.Add(item);
                }
                parent[key] = copy;
                return;
            }
            parent[key] = AddNumbers(current, delta);
        }

        /// <summary>current + delta for JSON numbers (long when both are integers and the sum fits); otherwise delta.</summary>
        public static object AddNumbers(object current, object delta)
        {
            if (!JsonValue.IsNumber(delta)) return ProfileJson.Clone(delta);
            if (!JsonValue.IsNumber(current)) return delta;
            if (current is long && delta is long)
            {
                long a = (long)current, b = (long)delta;
                long sum = unchecked(a + b);
                if (((a ^ sum) & (b ^ sum)) >= 0) return sum; // no overflow
            }
            return (JsonValue.AsDouble(current) ?? 0) + (JsonValue.AsDouble(delta) ?? 0);
        }

        private static bool TryCombine(string[] prefix, string relative, out string[] full)
        {
            full = null;
            string[] rel;
            if (string.IsNullOrEmpty(relative) || !ProfileJson.TryParsePath(relative.Trim(), out rel)) return false;
            full = new string[prefix.Length + rel.Length];
            Array.Copy(prefix, full, prefix.Length);
            Array.Copy(rel, 0, full, prefix.Length, rel.Length);
            return true;
        }

        /// <summary>
        /// POST /profiles/me/myprofile/visibility[/{selector}]: records the visibility of a selector without touching
        /// the data.
        /// </summary>
        public static WriteOutcome ApplyVisibility(ProfileDocument doc, string selector, string visibility)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            string[] path;
            if (!ProfileJson.TryParsePath(selector, out path)) return WriteOutcome.Fail("malformed selector '" + selector + "'");
            var outcome = new WriteOutcome();
            if (string.IsNullOrEmpty(visibility))
            {
                outcome.Notes.Add("no visibility given");
                return outcome;
            }
            outcome.Changed = RecordVisibility(doc, path, visibility);
            return outcome;
        }

        /// <summary>
        /// Remembers visibility per selector (root form {"*":"restricted"} is merged key by key). Visibility is not
        /// enforced: everything is local to the player. Returns true if something changed.
        /// </summary>
        private static bool RecordVisibility(ProfileDocument doc, string[] path, string visibility)
        {
            if (doc.Visibility == null) doc.Visibility = ProfileJson.NewObject();
            bool changed = false;
            object parsed;
            if (path.Length == 0 && MiniJson.TryParse(visibility, out parsed) && parsed is Dictionary<string, object>)
            {
                foreach (var kv in (Dictionary<string, object>)parsed)
                    changed |= SetVisibility(doc, kv.Key, kv.Value);
                return changed;
            }
            return SetVisibility(doc, path.Length == 0 ? "*" : ProfileJson.FormatPath(path), visibility);
        }

        internal static bool SetVisibility(ProfileDocument doc, string key, object value)
        {
            if (doc.Visibility == null) doc.Visibility = ProfileJson.NewObject();
            object old;
            if (doc.Visibility.TryGetValue(key, out old) && JsonValue.DeepEquals(old, value)) return false;
            doc.Visibility[key] = value;
            return true;
        }
    }
}
