using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace McvsOffline.Patcher.Tools
{
    /// <summary>
    /// Finds Android build-tools (apksigner, zipalign): --build-tools &lt;dir&gt;, then PATH, then the newest
    /// <c>$ANDROID_HOME/build-tools/*</c> (or ANDROID_SDK_ROOT). apksigner needs Java on PATH.
    /// </summary>
    public sealed class BuildTools
    {
        private BuildTools(string apksigner, string zipalign)
        {
            ApkSigner = apksigner;
            ZipAlign = zipalign;
        }

        /// <summary>Path of apksigner (a shell/batch wrapper), or null.</summary>
        public string ApkSigner { get; }

        /// <summary>Path of zipalign, or null.</summary>
        public string ZipAlign { get; }

        public static BuildTools Locate(string explicitDir)
        {
            var dirs = new List<string>();
            if (!string.IsNullOrEmpty(explicitDir)) dirs.Add(explicitDir);
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            dirs.AddRange(path.Split(Path.PathSeparator).Where(d => d.Length > 0));
            foreach (string variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
            {
                string sdk = Environment.GetEnvironmentVariable(variable);
                string buildTools = string.IsNullOrEmpty(sdk) ? null : Path.Combine(sdk, "build-tools");
                if (buildTools == null || !Directory.Exists(buildTools)) continue;
                dirs.AddRange(Directory.GetDirectories(buildTools).OrderByDescending(d => ParseVersion(Path.GetFileName(d))));
            }

            bool windows = OperatingSystem.IsWindows();
            string apksigner = Find(dirs, windows ? new[] { "apksigner.bat" } : new[] { "apksigner" });
            string zipalign = Find(dirs, windows ? new[] { "zipalign.exe" } : new[] { "zipalign" });
            if (!string.IsNullOrEmpty(explicitDir) && apksigner == null && zipalign == null)
                throw new ArgumentException("--build-tools " + explicitDir + " contains neither apksigner nor zipalign");
            return new BuildTools(apksigner, zipalign);
        }

        private static string Find(IEnumerable<string> dirs, string[] names)
        {
            foreach (string dir in dirs)
            {
                foreach (string name in names)
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        private static Version ParseVersion(string text)
        {
            Version v;
            return Version.TryParse(text.Split('-')[0], out v) ? v : new Version(0, 0);
        }

        /// <summary>Runs a tool; returns exit code, with stdout+stderr collected (JAVA_TOOL_OPTIONS chatter dropped).</summary>
        public static int Run(string exe, IEnumerable<string> arguments, IDictionary<string, string> environment, out string output)
        {
            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string a in arguments) info.ArgumentList.Add(a);
            if (environment != null)
            {
                foreach (var kv in environment) info.Environment[kv.Key] = kv.Value;
            }
            var text = new StringBuilder();
            using (var process = new Process { StartInfo = info })
            {
                process.OutputDataReceived += (s, e) => Append(text, e.Data);
                process.ErrorDataReceived += (s, e) => Append(text, e.Data);
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                output = text.ToString().TrimEnd();
                return process.ExitCode;
            }
        }

        private static void Append(StringBuilder text, string line)
        {
            if (line == null || line.StartsWith("Picked up JAVA_TOOL_OPTIONS", StringComparison.Ordinal)) return;
            lock (text) text.AppendLine(line);
        }
    }
}
