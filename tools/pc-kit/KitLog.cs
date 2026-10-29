using System;
using System.Globalization;
using System.IO;
using System.Text;
using McvsOffline.PcSpike;

namespace McvsOffline.PcKit
{
    /// <summary>
    /// The kit's own record, <c>pc-kit.log</c> (docs/design/pc-port.md §9.2): every command, what it found, wrote and removed;
    /// also echoed to the console. It lives in <c>&lt;install&gt;\McvsOffline\kit\</c>, or in <c>%TEMP%\McvsOffline\kit\</c> before
    /// that folder exists and from the start of an <c>uninstall --remove-data</c>. Each line is appended and the file closed again,
    /// and the log never creates a folder on its own after <see cref="ContinueIn"/>: so deleting McvsOffline\ is never blocked
    /// by our own open file (Windows) and the folder is not recreated by the next line.
    /// </summary>
    public sealed class KitLog : IToolLog
    {
        public const string FileName = "pc-kit.log";

        private readonly object gate = new object();
        private readonly TextWriter output;
        private readonly TextWriter error;
        private readonly Func<DateTime> now;
        private string path;

        public KitLog(string folder, TextWriter output, TextWriter error, Func<DateTime> now = null)
        {
            this.output = output;
            this.error = error;
            this.now = now ?? (() => DateTime.Now);
            path = Open(folder);
        }

        /// <summary>The file lines go to now, or null when no folder could be created.</summary>
        public string FilePath
        {
            get { lock (gate) return path; }
        }

        /// <summary>Moves the log: one line in the old file names the new one, the next lines go to <paramref name="folder"/>.</summary>
        public void ContinueIn(string folder)
        {
            string next = Open(folder);
            lock (gate)
            {
                if (next == null || string.Equals(next, path, StringComparison.Ordinal)) return;
                Append(path, Line("INFO", "log continues in " + next));
                string previous = path;
                path = next;
                Append(path, Line("INFO", "log continued from " + (previous ?? "(nowhere)")));
            }
        }

        public void Info(string message)
        {
            Write("INFO", message);
        }

        public void Warn(string message)
        {
            Write("WARN", message);
        }

        public void Error(string message)
        {
            Write("ERROR", message);
        }

        /// <summary>Only into the file (details the console does not need).</summary>
        public void Detail(string message)
        {
            lock (gate) Append(path, Line("DETAIL", message));
        }

        private void Write(string level, string message)
        {
            lock (gate)
            {
                try
                {
                    if (level == "INFO") output.WriteLine(message);
                    else error.WriteLine(level.ToLowerInvariant() + ": " + message);
                }
                catch (IOException)
                {
                    // The console may already be going away (the window was closed); the file still gets the line.
                }
                Append(path, Line(level, message));
            }
        }

        private string Line(string level, string message)
        {
            return now().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + level + " " + message;
        }

        private static string Open(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            try
            {
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, FileName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void Append(string file, string line)
        {
            if (file == null || !Directory.Exists(Path.GetDirectoryName(file))) return;
            try
            {
                File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }
    }
}
