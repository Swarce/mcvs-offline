using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace McvsOffline.PcSpike
{
    /// <summary>
    /// The kit's own record (<c>pc-spike.log</c> in the data folder): every command, what it wrote, backed up or restored,
    /// and what it found. 'collect' ships it, so the log says what happened even when the owner forgets.
    /// Also echoed to the console.
    /// </summary>
    public sealed class SpikeLog
    {
        public const string FileName = "pc-spike.log";

        private readonly object gate = new object();
        private readonly string path;
        private readonly bool echo;

        public SpikeLog(string dataDir, bool echo = true)
        {
            this.echo = echo;
            if (string.IsNullOrEmpty(dataDir)) return;
            try
            {
                Directory.CreateDirectory(dataDir);
                path = Path.Combine(dataDir, FileName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                path = null;
            }
        }

        public string FilePath => path;

        public void Info(string message) => Write("INFO", message);

        public void Warn(string message) => Write("WARN", message);

        public void Error(string message) => Write("ERROR", message);

        /// <summary>A finding the summary should show (also logged): "RESULT key: value".</summary>
        public void Result(string key, string value) => Write("RESULT", key + ": " + value);

        private void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + level + " " + message;
            lock (gate)
            {
                if (echo)
                {
                    // The console may already be going away (the window was closed); the file still gets the line.
                    try
                    {
                        if (level == "ERROR" || level == "WARN") Console.Error.WriteLine(level.ToLowerInvariant() + ": " + message);
                        else Console.WriteLine(message);
                    }
                    catch (IOException)
                    {
                    }
                }
                if (path == null) return;
                try
                {
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
