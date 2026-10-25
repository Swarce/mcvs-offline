using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

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

        /// <summary>Waits until everything written so far is stored (asynchronous sinks); false on timeout.</summary>
        public virtual bool Flush(int timeoutMs) { return true; }

        /// <summary>Crash path: stores what is queued on the calling thread, waiting at most <paramref name="budgetMs"/> for a lock.</summary>
        public virtual bool FlushNow(int budgetMs) { return true; }

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

    /// <summary>A <see cref="SafeLogFile"/> as a sink: enqueues formatted entries; the file is written by its own thread.</summary>
    public sealed class FileLogSink : LogSink
    {
        private readonly SafeLogFile file;

        public FileLogSink(string path, LogLevel minLevel, long maxBytes = 5L * 1024 * 1024, int maxFiles = 5, SafeLogFileOptions options = null)
            : base(minLevel)
        {
            options = options ?? new SafeLogFileOptions();
            options.MaxBytes = maxBytes;
            options.MaxFiles = maxFiles;
            file = new SafeLogFile(path, options);
        }

        public SafeLogFile File { get { return file; } }

        public string Path { get { return file.Path; } }

        public override void Write(LogEntry entry)
        {
            file.AppendLine(entry.Format());
        }

        public override bool Flush(int timeoutMs)
        {
            return file.Flush(timeoutMs);
        }

        public override bool FlushNow(int budgetMs)
        {
            return file.FlushNow(budgetMs);
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

        /// <summary>Flushes every sink (see <see cref="LogSink.Flush"/>); never throws.</summary>
        public bool Flush(int timeoutMs)
        {
            bool all = true;
            foreach (var sink in sinks)
            {
                try { all &= sink.Flush(timeoutMs); } catch (Exception) { all = false; }
            }
            return all;
        }

        /// <summary>Crash path (see <see cref="LogSink.FlushNow"/>); never throws.</summary>
        public void FlushNow(int budgetMs)
        {
            foreach (var sink in sinks)
            {
                try { sink.FlushNow(budgetMs); } catch (Exception) { }
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
}
