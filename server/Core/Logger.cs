using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Server.Core
{
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4,
        None = 5,
    }

    public sealed class LogEntry
    {
        public DateTime TimeUtc;
        public LogLevel Level;
        public string Category;
        public string Message;
        public Exception Exception;
        public int ThreadId;

        /// <summary>"2026-10-25T10:00:00.123Z INFO  [auth] message" (+ exception on following lines).</summary>
        public string Format()
        {
            var sb = new StringBuilder(64 + (Message == null ? 0 : Message.Length));
            sb.Append(TimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append(' ');
            sb.Append(LevelName(Level)).Append(" [").Append(Category ?? "-").Append("] ");
            sb.Append(Message);
            if (Exception != null) sb.Append('\n').Append(Exception);
            return sb.ToString();
        }

        public static string LevelName(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Trace: return "TRACE";
                case LogLevel.Debug: return "DEBUG";
                case LogLevel.Info: return "INFO ";
                case LogLevel.Warn: return "WARN ";
                case LogLevel.Error: return "ERROR";
                default: return "     ";
            }
        }

        /// <summary>Parses "trace|debug|info|warn|warning|error|none" (case-insensitive).</summary>
        public static LogLevel ParseLevel(string text, LogLevel defaultLevel)
        {
            if (string.IsNullOrEmpty(text)) return defaultLevel;
            switch (text.Trim().ToLowerInvariant())
            {
                case "trace": return LogLevel.Trace;
                case "debug": return LogLevel.Debug;
                case "info": return LogLevel.Info;
                case "warn":
                case "warning": return LogLevel.Warn;
                case "error": return LogLevel.Error;
                case "none":
                case "off": return LogLevel.None;
                default: return defaultLevel;
            }
        }
    }

    /// <summary>A log destination with its own minimum level. Implementations must be thread-safe and never throw.</summary>
    public abstract class LogSink : IDisposable
    {
        protected LogSink(LogLevel minLevel)
        {
            MinLevel = minLevel;
        }

        public LogLevel MinLevel { get; set; }

        public abstract void Write(LogEntry entry);

        public virtual void Dispose() { }
    }

    /// <summary>Writes to stdout (on Android, Xamarin forwards stdout to logcat).</summary>
    public sealed class ConsoleLogSink : LogSink
    {
        private readonly object gate = new object();

        public ConsoleLogSink(LogLevel minLevel) : base(minLevel) { }

        public override void Write(LogEntry entry)
        {
            string line = entry.Format();
            lock (gate)
            {
                try { Console.WriteLine(line); } catch (Exception) { }
            }
        }
    }

    /// <summary>Appends to a file and rotates it (name.log → name.log.1 ... name.log.N) when it grows too large.</summary>
    public sealed class RotatingFileLogSink : LogSink
    {
        private readonly RotatingFile file;

        public RotatingFileLogSink(string path, LogLevel minLevel, long maxBytes = 5L * 1024 * 1024, int maxFiles = 5)
            : base(minLevel)
        {
            file = new RotatingFile(path, maxBytes, maxFiles);
        }

        public string Path { get { return file.Path; } }

        public override void Write(LogEntry entry)
        {
            file.AppendLine(entry.Format());
        }

        public override void Dispose()
        {
            file.Dispose();
        }
    }

    /// <summary>Keeps entries in memory (tests, diagnostics endpoints). Bounded.</summary>
    public sealed class MemoryLogSink : LogSink
    {
        private readonly object gate = new object();
        private readonly List<LogEntry> entries = new List<LogEntry>();
        private readonly int capacity;

        public MemoryLogSink(LogLevel minLevel = LogLevel.Trace, int capacity = 10000) : base(minLevel)
        {
            this.capacity = capacity;
        }

        public override void Write(LogEntry entry)
        {
            lock (gate)
            {
                if (entries.Count >= capacity) entries.RemoveAt(0);
                entries.Add(entry);
            }
        }

        public List<LogEntry> Snapshot()
        {
            lock (gate) return new List<LogEntry>(entries);
        }

        /// <summary>True if any entry's formatted text contains <paramref name="text"/>.</summary>
        public bool Contains(string text)
        {
            foreach (var e in Snapshot())
            {
                if (e.Format().IndexOf(text, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Root logger: fans entries out to sinks. Obtain a category logger with <see cref="For"/>.
    /// Logging never throws.
    /// </summary>
    public sealed class Logger : IDisposable
    {
        private readonly object gate = new object();
        private volatile LogSink[] sinks = new LogSink[0];   // replaced as a whole under the lock, read without it
        private volatile int minLevel = (int)LogLevel.None;

        public void AddSink(LogSink sink)
        {
            if (sink == null) throw new ArgumentNullException("sink");
            lock (gate)
            {
                var list = new List<LogSink>(sinks);
                list.Add(sink);
                sinks = list.ToArray();
                Recompute();
            }
        }

        public void RemoveSink(LogSink sink)
        {
            lock (gate)
            {
                var list = new List<LogSink>(sinks);
                list.Remove(sink);
                sinks = list.ToArray();
                Recompute();
            }
        }

        /// <summary>Call after changing a sink's MinLevel.</summary>
        public void Recompute()
        {
            lock (gate)
            {
                LogLevel min = LogLevel.None;
                foreach (var s in sinks)
                {
                    if (s.MinLevel < min) min = s.MinLevel;
                }
                minLevel = (int)min;
            }
        }

        public bool IsEnabled(LogLevel level)
        {
            return (int)level >= minLevel && level != LogLevel.None;
        }

        public Log For(string category)
        {
            return new Log(this, category);
        }

        public void Write(LogLevel level, string category, string message, Exception exception = null)
        {
            if (!IsEnabled(level)) return;
            var entry = new LogEntry
            {
                TimeUtc = DateTime.UtcNow,
                Level = level,
                Category = category,
                Message = message,
                Exception = exception,
                ThreadId = Thread.CurrentThread.ManagedThreadId,
            };
            LogSink[] current = sinks;
            foreach (var sink in current)
            {
                if (level < sink.MinLevel) continue;
                try { sink.Write(entry); } catch (Exception) { }
            }
        }

        public void Dispose()
        {
            LogSink[] current;
            lock (gate)
            {
                current = sinks;
                sinks = new LogSink[0];
                minLevel = (int)LogLevel.None;
            }
            foreach (var sink in current)
            {
                try { sink.Dispose(); } catch (Exception) { }
            }
        }
    }

    /// <summary>A logger bound to one category (usually the module name, e.g. "auth").</summary>
    public sealed class Log
    {
        private readonly Logger logger;

        public Log(Logger logger, string category)
        {
            this.logger = logger;
            Category = category;
        }

        public string Category { get; private set; }

        public Logger Logger { get { return logger; } }

        public bool IsEnabled(LogLevel level) { return logger != null && logger.IsEnabled(level); }

        public void Trace(string message) { Write(LogLevel.Trace, message, null); }

        public void Debug(string message) { Write(LogLevel.Debug, message, null); }

        public void Info(string message) { Write(LogLevel.Info, message, null); }

        public void Warn(string message, Exception exception = null) { Write(LogLevel.Warn, message, exception); }

        public void Error(string message, Exception exception = null) { Write(LogLevel.Error, message, exception); }

        public void Write(LogLevel level, string message, Exception exception)
        {
            if (logger != null) logger.Write(level, Category, message, exception);
        }

        /// <summary>A logger that discards everything.</summary>
        public static Log Null { get { return new Log(null, "null"); } }
    }

    /// <summary>
    /// Thread-safe line appender with size-based rotation, shared by the file log and the unknown-endpoint log.
    /// The file is opened with FileShare.ReadWrite so it can be tailed while the server runs.
    /// </summary>
    public sealed class RotatingFile : IDisposable
    {
        private readonly object gate = new object();
        private readonly long maxBytes;
        private readonly int maxFiles;
        private FileStream stream;
        private bool failed;

        public RotatingFile(string path, long maxBytes, int maxFiles)
        {
            Path = System.IO.Path.GetFullPath(path);
            this.maxBytes = Num.Max(4096L, maxBytes);
            this.maxFiles = Num.Max(1, maxFiles);
        }

        public string Path { get; private set; }

        public void AppendLine(string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            lock (gate)
            {
                if (failed) return;
                try
                {
                    if (stream == null) Open();
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                    if (stream.Length >= maxBytes) Rotate();
                }
                catch (Exception)
                {
                    // A broken log file must never break the server; give up on this file.
                    failed = true;
                    CloseStream();
                }
            }
        }

        public void Dispose()
        {
            lock (gate) CloseStream();
        }

        private void Open()
        {
            string dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        }

        private void Rotate()
        {
            CloseStream();
            string oldest = Path + "." + maxFiles.ToString(CultureInfo.InvariantCulture);
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int i = maxFiles - 1; i >= 1; i--)
            {
                string from = Path + "." + i.ToString(CultureInfo.InvariantCulture);
                if (File.Exists(from)) FileSystem.Rename(from, Path + "." + (i + 1).ToString(CultureInfo.InvariantCulture));
            }
            FileSystem.Rename(Path, Path + ".1");
            Open();
        }

        private void CloseStream()
        {
            if (stream == null) return;
            try { stream.Dispose(); } catch (Exception) { }
            stream = null;
        }
    }
}
