using System;
using System.Collections.Generic;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Services.Admin.Saves;

namespace McvsOffline.Server.Services.Admin.Logs
{
    /// <summary>One log entry: time, level, category, message (continuation lines included).</summary>
    internal sealed class LogTailEntry
    {
        /// <summary>The time as written ("2026-10-26T10:15:00.123Z", or "10:15:00.123" in game.log of builds up to 13), or null.</summary>
        public string Time { get; set; }

        /// <summary>TRACE, DEBUG, INFO, WARN, ERROR, or "?" for text before the first entry.</summary>
        public string Level { get; set; }

        public string Category { get; set; }

        public string Message { get; set; }

        public Dictionary<string, object> ToJson()
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["t"] = Time;
            o["l"] = Level;
            o["c"] = Category;
            o["m"] = Message;
            return o;
        }
    }

    /// <summary>The end of a log file, parsed.</summary>
    internal sealed class LogTailResult
    {
        public long Size { get; set; }

        /// <summary>Bytes read from the end of the file.</summary>
        public long Window { get; set; }

        /// <summary>The window starts after the beginning of the file (its first, partial line was dropped).</summary>
        public bool TruncatedHead { get; set; }

        public List<LogTailEntry> Entries { get; set; }
    }

    /// <summary>
    /// Reads the last megabyte of a log file and splits it into entries (design §7.1). An entry starts at a line of
    /// server.log/bootstrap.log (<c>LogEntry.Format</c>: <c>2026-10-26T10:15:00.123Z INFO  [cat] message</c>) or game.log
    /// (<c>GameLogFormat</c>: <c>2026-10-26T10:15:00.123Z Warning [cat] message</c>, up to build 13 <c>10:15:00.123 Warning
    /// [cat] message</c>) and continues over the following lines that start
    /// no entry (exceptions, multi-line messages); lines before the first entry form one entry of level "?". Hand-written
    /// character checks, no Regex. Every message goes through <see cref="LogRedaction.Redact"/> (files of older builds).
    /// </summary>
    internal static class LogTail
    {
        public const int WindowBytes = 1024 * 1024;
        public const int RawBytes = 512 * 1024;
        public const int MaxMessageChars = 16 * 1024;
        public const int MaxLines = 2000;

        /// <summary>The last <paramref name="lines"/> entries at or above <paramref name="min"/> whose text contains <paramref name="query"/>.</summary>
        public static LogTailResult Read(string path, int lines, LogLevel min, string query)
        {
            long size;
            byte[] bytes = AdminFiles.ReadTail(path, WindowBytes, out size);
            int start = 0;
            bool truncated = size > bytes.Length;
            if (truncated)
            {
                // The window starts mid-file: drop the partial first line (and any partial UTF-8 sequence with it).
                int newline = Array.IndexOf(bytes, (byte)'\n');
                start = newline < 0 ? bytes.Length : newline + 1;
            }
            string text = AdminFiles.Utf8(bytes, start, bytes.Length - start);
            List<LogTailEntry> all = Parse(text);
            var picked = new List<LogTailEntry>();
            for (int i = all.Count - 1; i >= 0 && picked.Count < lines; i--)
            {
                LogTailEntry e = all[i];
                e.Message = Clean(e.Message);   // before the search: it must not find what the page cannot see
                if (!Passes(e, min, query)) continue;
                picked.Add(e);
            }
            picked.Reverse();
            return new LogTailResult { Size = size, Window = bytes.Length, TruncatedHead = truncated, Entries = picked };
        }

        /// <summary>The last 512 KiB of a file as text, redacted (crash files and thread dumps).</summary>
        public static string Raw(string path, out long size, out bool truncated)
        {
            byte[] bytes = AdminFiles.ReadTail(path, RawBytes, out size);
            truncated = size > bytes.Length;
            int start = 0;
            if (truncated)
            {
                int newline = Array.IndexOf(bytes, (byte)'\n');
                start = newline < 0 ? 0 : newline + 1;
            }
            return LogRedaction.Redact(AdminFiles.Utf8(bytes, start, bytes.Length - start));
        }

        public static bool Passes(LogTailEntry e, LogLevel min, string query)
        {
            if (e.Level != "?" && LevelOf(e.Level) < min) return false;
            if (string.IsNullOrEmpty(query)) return true;
            return (e.Message != null && e.Message.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                   || (e.Category != null && e.Category.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static LogLevel LevelOf(string level)
        {
            switch (level)
            {
                case "TRACE": return LogLevel.Trace;
                case "DEBUG": return LogLevel.Debug;
                case "INFO": return LogLevel.Info;
                case "WARN": return LogLevel.Warn;
                case "ERROR": return LogLevel.Error;
                default: return LogLevel.Trace;
            }
        }

        /// <summary>Splits a text into entries (both formats; anything before the first entry is one "?" entry).</summary>
        public static List<LogTailEntry> Parse(string text)
        {
            var entries = new List<LogTailEntry>();
            LogTailEntry current = null;
            StringBuilder message = null;
            int pos = 0;
            while (pos < text.Length)
            {
                int end = text.IndexOf('\n', pos);
                if (end < 0) end = text.Length;
                int lineEnd = end > pos && text[end - 1] == '\r' ? end - 1 : end;
                string line = text.Substring(pos, lineEnd - pos);
                pos = end + 1;

                LogTailEntry started = ServerLine(line) ?? GameLine(line);
                if (started != null)
                {
                    if (current != null) Close(current, message, entries);
                    current = started;
                    message = new StringBuilder(started.Message);
                    continue;
                }
                if (current == null)
                {
                    if (line.Length == 0 && pos >= text.Length) break;
                    current = new LogTailEntry { Level = "?", Message = "" };
                    message = new StringBuilder(line);
                    continue;
                }
                if (message.Length < MaxMessageChars + 1) message.Append('\n').Append(line);
            }
            if (current != null) Close(current, message, entries);
            return entries;
        }

        private static void Close(LogTailEntry entry, StringBuilder message, List<LogTailEntry> entries)
        {
            // A file ends with a newline: the empty last "line" is not part of the message.
            while (message.Length > 0 && message[message.Length - 1] == '\n') message.Length--;
            entry.Message = message.ToString();
            entries.Add(entry);
        }

        /// <summary>"2026-10-26T10:15:00.123Z LEVEL [cat] message": 24-character time, 5-character level (padded).</summary>
        private static LogTailEntry ServerLine(string line)
        {
            if (line.Length < 33 || line[24] != ' ' || line[30] != ' ' || line[31] != '[') return null;
            if (!IsoTime(line)) return null;
            string level = line.Substring(25, 5).TrimEnd();
            if (level.Length == 0) level = "?";
            else if (level != "TRACE" && level != "DEBUG" && level != "INFO" && level != "WARN" && level != "ERROR") return null;
            int close = line.IndexOf(']', 32);
            if (close < 0 || (close + 1 < line.Length && line[close + 1] != ' ')) return null;
            return new LogTailEntry
            {
                Time = line.Substring(0, 24),
                Level = level,
                Category = line.Substring(32, close - 32),
                Message = close + 2 <= line.Length ? line.Substring(close + 2) : "",
            };
        }

        /// <summary>
        /// "2026-10-26T10:15:00.123Z Warning [cat] message" (GameLogFormat, UTC) or "10:15:00.123 Warning [cat] message"
        /// (GameLogBridge up to build 13, local time).
        /// </summary>
        private static LogTailEntry GameLine(string line)
        {
            int time = IsoTime(line) ? 24 : ClockTime(line) ? 12 : 0;
            if (time == 0 || line.Length < time + 4 || line[time] != ' ') return null;
            int start = time + 1;
            int p = start;
            while (p < line.Length && (char.IsLetterOrDigit(line[p]) || line[p] == '_')) p++;
            if (p == start || p + 1 >= line.Length || line[p] != ' ' || line[p + 1] != '[') return null;
            string word = line.Substring(start, p - start);
            int close = line.IndexOf(']', p + 2);
            if (close < 0 || (close + 1 < line.Length && line[close + 1] != ' ')) return null;
            return new LogTailEntry
            {
                Time = line.Substring(0, time),
                Level = GameLevel(word),
                Category = line.Substring(p + 2, close - p - 2),
                Message = close + 2 <= line.Length ? line.Substring(close + 2) : "",
            };
        }

        /// <summary>Gameloft.Logs levels: Fatal/Error → ERROR, Warning → WARN, Info → INFO, Debug/Verbose → DEBUG.</summary>
        private static string GameLevel(string word)
        {
            switch (word.ToLowerInvariant())
            {
                case "fatal":
                case "error": return "ERROR";
                case "warning":
                case "warn": return "WARN";
                case "info": return "INFO";
                case "debug":
                case "verbose":
                case "trace": return "DEBUG";
                default: return word.ToUpperInvariant();
            }
        }

        private static string Clean(string message)
        {
            if (message == null) return "";
            if (message.Length > MaxMessageChars) message = message.Substring(0, MaxMessageChars) + "…";
            return LogRedaction.Redact(message);
        }

        /// <summary>The line starts with "2026-10-26T10:15:00.123Z".</summary>
        private static bool IsoTime(string line)
        {
            return line.Length >= 24 && IsDigits(line, 0, 4) && line[4] == '-' && IsDigits(line, 5, 2) && line[7] == '-' && IsDigits(line, 8, 2)
                   && line[10] == 'T' && IsDigits(line, 11, 2) && line[13] == ':' && IsDigits(line, 14, 2) && line[16] == ':'
                   && IsDigits(line, 17, 2) && line[19] == '.' && IsDigits(line, 20, 3) && line[23] == 'Z';
        }

        /// <summary>The line starts with "10:15:00.123".</summary>
        private static bool ClockTime(string line)
        {
            return line.Length >= 12 && IsDigits(line, 0, 2) && line[2] == ':' && IsDigits(line, 3, 2) && line[5] == ':' && IsDigits(line, 6, 2)
                   && line[8] == '.' && IsDigits(line, 9, 3);
        }

        private static bool IsDigits(string s, int start, int count)
        {
            for (int i = start; i < start + count; i++)
            {
                if (s[i] < '0' || s[i] > '9') return false;
            }
            return true;
        }
    }
}
