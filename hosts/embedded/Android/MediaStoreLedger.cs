// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent (the MediaStore delete is passed in): compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Embedded.Android
{
    /// <summary>
    /// Housekeeping of the files the settings page shares (design §7.6): every MediaStore Downloads row the host inserts
    /// (<c>mcvs-logs-*.zip</c>, <c>mcvs-save-*.txt|json</c> in Download/MCVS-Offline/) is recorded with its kind and time in
    /// <c>&lt;data&gt;/admin/mediastore.json</c>; after each insert the rows of that kind beyond the newest
    /// <see cref="KeepPerKind"/> are deleted (<c>ContentResolver.delete(uri, null, null)</c>: the game owns them, no permission
    /// needed on Android 10+). The log mirror's pruning never sees these rows. A row the delete no longer finds is dropped from
    /// the ledger; one whose delete fails stays for the next time. Thread-safe; never throws.
    /// </summary>
    internal sealed class MediaStoreLedger
    {
        public const int KeepPerKind = 5;
        public const string FileName = "mediastore.json";

        private static readonly object gate = new object();
        private readonly string path;

        /// <param name="dataDir">The server's data directory (McvsOffline/); the ledger is admin/mediastore.json in it.</param>
        public MediaStoreLedger(string dataDir)
        {
            path = Path.Combine(Path.Combine(dataDir, "admin"), FileName);
        }

        /// <summary>admin/mediastore.json.</summary>
        public string FilePath { get { return path; } }

        /// <summary>One recorded row.</summary>
        public sealed class Row
        {
            public string Uri;
            public string Kind;
            public string Name;
            public string Time;
        }

        /// <summary>"logs" (mcvs-logs-*), "save" (mcvs-save-*) or "other".</summary>
        public static string KindOf(string displayName)
        {
            string name = displayName ?? "";
            if (name.StartsWith("mcvs-logs-", StringComparison.Ordinal)) return "logs";
            if (name.StartsWith("mcvs-save-", StringComparison.Ordinal)) return "save";
            return "other";
        }

        /// <summary>
        /// Records a new row, then deletes the rows of its kind beyond the newest <see cref="KeepPerKind"/>.
        /// <paramref name="delete"/>: rows deleted for a content URI (0: it no longer exists), or a negative number on failure.
        /// Returns the URIs dropped from the ledger.
        /// </summary>
        public List<string> Record(string uri, string displayName, DateTime nowUtc, Func<string, int> delete)
        {
            var dropped = new List<string>();
            if (string.IsNullOrEmpty(uri)) return dropped;
            lock (gate)
            {
                try
                {
                    List<Row> rows = Load();
                    rows.RemoveAll(r => r.Uri == uri);
                    string kind = KindOf(displayName);
                    rows.Add(new Row { Uri = uri, Kind = kind, Name = displayName, Time = TimeFormat.Iso8601Millis(nowUtc) });
                    var ofKind = rows.FindAll(r => r.Kind == kind);
                    for (int i = 0; i < ofKind.Count - KeepPerKind; i++)
                    {
                        int result;
                        try
                        {
                            result = delete == null ? -1 : delete(ofKind[i].Uri);
                        }
                        catch (Exception)
                        {
                            result = -1;
                        }
                        if (result < 0) continue;
                        rows.Remove(ofKind[i]);
                        dropped.Add(ofKind[i].Uri);
                    }
                    Save(rows);
                }
                catch (Exception)
                {
                }
            }
            return dropped;
        }

        /// <summary>The recorded rows, oldest first (tests, diagnostics).</summary>
        public List<Row> Rows()
        {
            lock (gate)
            {
                try
                {
                    return Load();
                }
                catch (Exception)
                {
                    return new List<Row>();
                }
            }
        }

        private List<Row> Load()
        {
            var rows = new List<Row>();
            string text = File.Exists(path) ? AtomicFile.ReadAllTextOrNull(path) : null;
            if (string.IsNullOrEmpty(text)) return rows;
            object parsed;
            if (!MiniJson.TryParse(text, out parsed)) return rows;   // a damaged ledger starts over
            var root = parsed as Dictionary<string, object>;
            var list = root == null ? null : JsonValue.GetArray(root, "rows");
            if (list == null) return rows;
            foreach (object item in list)
            {
                var o = item as Dictionary<string, object>;
                if (o == null) continue;
                string uri = JsonValue.GetString(o, "uri");
                if (string.IsNullOrEmpty(uri)) continue;
                rows.Add(new Row { Uri = uri, Kind = JsonValue.GetString(o, "kind") ?? "other", Name = JsonValue.GetString(o, "name"), Time = JsonValue.GetString(o, "time") });
            }
            return rows;
        }

        private void Save(List<Row> rows)
        {
            var list = new List<object>();
            foreach (Row r in rows)
            {
                var o = new Dictionary<string, object>(StringComparer.Ordinal);
                o["uri"] = r.Uri;
                o["kind"] = r.Kind;
                o["name"] = r.Name;
                o["time"] = r.Time;
                list.Add(o);
            }
            var root = new Dictionary<string, object>(StringComparer.Ordinal);
            root["version"] = 1L;
            root["rows"] = list;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AtomicFile.WriteAllText(path, MiniJson.Serialize(root, true));
        }
    }
}
