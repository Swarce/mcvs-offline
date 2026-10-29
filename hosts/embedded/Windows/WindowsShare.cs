#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests, never into the Android DLL.
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>
    /// "Share" on PC (docs/design/pc-port.md §6.3, C15): there is no share sheet, so a shared file or text becomes a file in
    /// <c>&lt;DataDir&gt;\exports\</c> (<c>McvsOffline\exports</c>) and Explorer opens on it (<c>explorer.exe /select,"&lt;file&gt;"</c>).
    /// An existing name, compared ignoring case (NTFS folds case, §4.6), gets a " (2)", " (3)"... suffix, so nothing is
    /// overwritten. Results: Ok with method <c>explorer</c> (Explorer started) or <c>file</c> (the file is there, Explorer did not
    /// start), <c>PublicPath = "McvsOffline\exports\&lt;name&gt;"</c>. Never throws.
    /// </summary>
    public sealed class WindowsShare
    {
        public const string FolderName = "exports";

        /// <summary>The longest file name stem written for a text.</summary>
        internal const int MaxStemChars = 100;

        private readonly Func<string> exportsDir;
        private readonly Action<string, string> launch;
        private readonly Action<string> warn;

        /// <param name="exportsDir">The exports folder (created when missing), or null when the data folder is unknown.</param>
        /// <param name="launch">(program, arguments): starts a program (tests record it); null: <c>Process.Start</c> without ShellExecute.</param>
        /// <param name="warn">Where failures go; may be null.</param>
        public WindowsShare(Func<string> exportsDir, Action<string, string> launch, Action<string> warn)
        {
            this.exportsDir = exportsDir;
            this.launch = launch ?? Start;
            this.warn = warn;
        }

        /// <summary>Copies <paramref name="path"/> to exports\<paramref name="displayName"/> and shows it in Explorer.</summary>
        public AdminShareResult ShareFile(string path, string displayName)
        {
            try
            {
                string dir = Folder();
                if (dir == null) return AdminShareResult.None("The MCVS Offline folder is unknown; nothing was copied.");
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return AdminShareResult.None("The file to share is missing.");
                string name = UniqueName(dir, Sanitize(string.IsNullOrEmpty(displayName) ? Path.GetFileName(path) : displayName, ""));
                string target = Path.Combine(dir, name);
                File.Copy(path, target, false);
                return Shown(target, name);
            }
            catch (Exception ex)
            {
                Warn("admin: the file could not be copied to " + FolderName + ": " + ex.GetType().Name + ": " + ex.Message);
                return AdminShareResult.None("The file could not be copied to McvsOffline\\" + FolderName + ".");
            }
        }

        /// <summary>Writes <paramref name="text"/> to exports\&lt;subject&gt;.txt and shows it in Explorer.</summary>
        public AdminShareResult ShareText(string subject, string text)
        {
            try
            {
                string dir = Folder();
                if (dir == null) return AdminShareResult.None("The MCVS Offline folder is unknown; nothing was written.");
                string name = UniqueName(dir, Sanitize(subject, ".txt"));
                string target = Path.Combine(dir, name);
                using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    byte[] bytes = new UTF8Encoding(true).GetBytes(text ?? "");
                    byte[] bom = new UTF8Encoding(true).GetPreamble();
                    stream.Write(bom, 0, bom.Length);
                    stream.Write(bytes, 0, bytes.Length);
                }
                return Shown(target, name);
            }
            catch (Exception ex)
            {
                Warn("admin: the text could not be written to " + FolderName + ": " + ex.GetType().Name + ": " + ex.Message);
                return AdminShareResult.None("The text could not be written to McvsOffline\\" + FolderName + ".");
            }
        }

        private AdminShareResult Shown(string target, string name)
        {
            string publicPath = EmbeddedPaths.DataFolderName + "\\" + FolderName + "\\" + name;
            try
            {
                launch("explorer.exe", "/select,\"" + target + "\"");
                return new AdminShareResult { Ok = true, Method = "explorer", PublicPath = publicPath, Message = "Saved as " + publicPath + "; Explorer shows it." };
            }
            catch (Exception ex)
            {
                Warn("admin: Explorer did not start: " + ex.GetType().Name + ": " + ex.Message);
                return new AdminShareResult { Ok = true, Method = "file", PublicPath = publicPath, Message = "Saved as " + publicPath + "." };
            }
        }

        private string Folder()
        {
            string dir = exportsDir == null ? null : exportsDir();
            if (string.IsNullOrEmpty(dir)) return null;
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// A Windows-safe file name: the characters <c>\/:*?"&lt;&gt;|</c> and control characters become '_', trailing dots and spaces
        /// go, a reserved device name (CON, PRN, AUX, NUL, COM1-9, LPT1-9, with or without an extension) gets a leading '_', the
        /// stem is at most <see cref="MaxStemChars"/> characters; <paramref name="extension"/> is appended when the name lacks it.
        /// Empty → "MCVS Offline".
        /// </summary>
        internal static string Sanitize(string name, string extension)
        {
            var sb = new StringBuilder();
            foreach (char c in (name ?? "").Trim())
            {
                bool bad = c < 0x20 || c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|';
                sb.Append(bad ? '_' : c);
            }
            string text = sb.ToString();
            if (!string.IsNullOrEmpty(extension) && text.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                text = text.Substring(0, text.Length - extension.Length);
            string ext = extension ?? "";
            if (ext.Length == 0)
            {
                int dot = text.LastIndexOf('.');
                if (dot > 0 && text.Length - dot <= 10)
                {
                    ext = text.Substring(dot);
                    text = text.Substring(0, dot);
                }
            }
            if (text.Length > MaxStemChars) text = text.Substring(0, MaxStemChars);
            text = text.TrimEnd('.', ' ');
            if (text.Length == 0) text = "MCVS Offline";
            if (IsReservedName(text)) text = "_" + text;
            return text + ext;
        }

        /// <summary>CON, PRN, AUX, NUL, COM1-COM9, LPT1-LPT9 (any case), also before an extension ("nul.txt").</summary>
        internal static bool IsReservedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            int dot = name.IndexOf('.');
            string stem = (dot < 0 ? name : name.Substring(0, dot)).TrimEnd(' ').ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL") return true;
            return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                   stem[3] >= '1' && stem[3] <= '9';
        }

        /// <summary><paramref name="name"/>, or "stem (2).ext", "stem (3).ext"... when a file of that name exists, ignoring case.</summary>
        internal static string UniqueName(string dir, string name)
        {
            var taken = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in Directory.GetFileSystemEntries(dir)) taken.Add(Path.GetFileName(entry));
            if (!taken.Contains(name)) return name;
            string ext = Path.GetExtension(name);
            string stem = name.Substring(0, name.Length - ext.Length);
            for (int n = 2; ; n++)
            {
                string candidate = stem + " (" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" + ext;
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        private static void Start(string program, string arguments)
        {
            using (Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false }))
            {
            }
        }

        private void Warn(string text)
        {
            try
            {
                if (warn != null) warn(text);
            }
            catch (Exception)
            {
            }
        }
    }
}
#endif
