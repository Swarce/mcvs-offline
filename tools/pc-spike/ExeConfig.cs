using System;
using System.Security;
using System.Text;

namespace McvsOffline.PcSpike
{
    /// <summary>
    /// <c>ModernCombatVersus.exe.config</c> for WP0 step 2 (pc-port.md §3.2 option A): one <c>&lt;codeBase&gt;</c> that sends
    /// the CLR's load of <c>EngineBridge</c> to our copy in a subfolder. The assemblies are not strong-named, so the
    /// version in <c>codeBase</c> is ignored and the href must lie under the application base (the exe folder), which a
    /// relative <c>McvsOffline/...</c> path does (critic note 6). Whether the CLR reads this file for the mixed-mode exe at
    /// all is exactly what the spike tests (unknown 1).
    /// </summary>
    public static class ExeConfig
    {
        public const string FileName = "ModernCombatVersus.exe.config";

        /// <summary>Written into the file so 'redirect-undo' only ever deletes our own config.</summary>
        public const string OwnerComment = "<!-- written by McvsOffline.PcSpike redirect-test; 'redirect-undo' removes it -->";

        /// <summary>The config text (UTF-8, CRLF).</summary>
        public static string Build(string assemblyName, Version version, string relativeHref)
        {
            if (string.IsNullOrEmpty(assemblyName)) throw new ArgumentException("assembly name required", nameof(assemblyName));
            if (version == null) throw new ArgumentNullException(nameof(version));
            if (string.IsNullOrEmpty(relativeHref) || relativeHref.Contains(":") || relativeHref.StartsWith("/", StringComparison.Ordinal) ||
                relativeHref.StartsWith("\\", StringComparison.Ordinal) || relativeHref.Contains(".."))
            {
                throw new ArgumentException("the codeBase href must be relative and under the exe folder: " + relativeHref, nameof(relativeHref));
            }
            string href = relativeHref.Replace('\\', '/');
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
            sb.Append(OwnerComment).Append("\r\n");
            sb.Append("<configuration>\r\n");
            sb.Append("  <runtime>\r\n");
            sb.Append("    <assemblyBinding xmlns=\"urn:schemas-microsoft-com:asm.v1\">\r\n");
            sb.Append("      <dependentAssembly>\r\n");
            sb.Append("        <assemblyIdentity name=\"").Append(SecurityElement.Escape(assemblyName)).Append("\" culture=\"neutral\" />\r\n");
            sb.Append("        <codeBase version=\"").Append(version.ToString(4))
              .Append("\" href=\"").Append(SecurityElement.Escape(href)).Append("\" />\r\n");
            sb.Append("      </dependentAssembly>\r\n");
            sb.Append("    </assemblyBinding>\r\n");
            sb.Append("  </runtime>\r\n");
            sb.Append("</configuration>\r\n");
            return sb.ToString();
        }

        public static bool IsOurs(string text)
        {
            return text != null && text.Contains(OwnerComment);
        }
    }
}
