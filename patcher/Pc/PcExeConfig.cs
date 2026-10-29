using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;
using System.Xml;

namespace McvsOffline.Patcher.Pc
{
    /// <summary>One <c>&lt;dependentAssembly&gt;</c> of the config: the assembly, its version and the relative href of our copy.</summary>
    public sealed class PcCodeBase
    {
        public PcCodeBase(string assemblyName, Version version, string href)
        {
            AssemblyName = assemblyName;
            Version = version;
            Href = href;
        }

        public string AssemblyName { get; }

        public Version Version { get; }

        /// <summary>Relative to the exe folder, with '/': "McvsOffline/bin/Game.dll".</summary>
        public string Href { get; }
    }

    /// <summary>
    /// <c>ModernCombatVersus.exe.config</c> for option A (docs/design/pc-port.md §7.4): one <c>&lt;codeBase&gt;</c> per patched
    /// assembly and the server, pointing into <c>McvsOffline/bin/</c>. A <c>&lt;codeBase&gt;</c> is consulted before probing and is
    /// the only way to skip the same-named stock DLL in the application base; the assemblies are not strong-named, so the
    /// version is ignored but required by the schema, and the href must lie under the exe folder. Nothing else goes in: no
    /// <c>&lt;startup&gt;</c>, no <c>&lt;probing&gt;</c>, no <c>&lt;loadFromRemoteSources&gt;</c>. UTF-8 without BOM, CRLF, final CRLF.
    /// </summary>
    public static class PcExeConfig
    {
        public const string FileName = "ModernCombatVersus.exe.config";

        /// <summary>Our marker; <see cref="IsOurs"/> looks for it, so uninstall never deletes a config somebody else wrote.</summary>
        public const string OwnerComment = "<!-- written by McvsOffline (PC install); 'uninstall' removes it; see McvsOffline/install.json -->";

        /// <summary>The WP0 spike's marker (tools/pc-spike/ExeConfig.cs OwnerComment): install replaces it, uninstall removes it.</summary>
        public const string SpikeOwnerComment = "<!-- written by McvsOffline.PcSpike redirect-test; 'redirect-undo' removes it -->";

        /// <summary>The folder of our copies, relative to the exe folder.</summary>
        public const string BinHrefFolder = "McvsOffline/bin/";

        /// <summary>The config text for <paramref name="codeBases"/>, in that order.</summary>
        public static string Build(IList<PcCodeBase> codeBases)
        {
            if (codeBases == null || codeBases.Count == 0) throw new ArgumentException("at least one codeBase is required", nameof(codeBases));
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
            sb.Append(OwnerComment).Append("\r\n");
            sb.Append("<configuration>\r\n");
            sb.Append("  <runtime>\r\n");
            sb.Append("    <assemblyBinding xmlns=\"urn:schemas-microsoft-com:asm.v1\">\r\n");
            foreach (PcCodeBase codeBase in codeBases)
            {
                if (string.IsNullOrEmpty(codeBase.AssemblyName)) throw new ArgumentException("assembly name required", nameof(codeBases));
                if (codeBase.Version == null) throw new ArgumentException(codeBase.AssemblyName + ": version required", nameof(codeBases));
                CheckHref(codeBase.Href);
                sb.Append("      <dependentAssembly>\r\n");
                sb.Append("        <assemblyIdentity name=\"").Append(SecurityElement.Escape(codeBase.AssemblyName)).Append("\" culture=\"neutral\" />\r\n");
                sb.Append("        <codeBase version=\"").Append(Version4(codeBase.Version))
                  .Append("\" href=\"").Append(SecurityElement.Escape(codeBase.Href)).Append("\" />\r\n");
                sb.Append("      </dependentAssembly>\r\n");
            }
            sb.Append("    </assemblyBinding>\r\n");
            sb.Append("  </runtime>\r\n");
            sb.Append("</configuration>\r\n");
            return sb.ToString();
        }

        /// <summary>The bytes written: UTF-8 without BOM.</summary>
        public static byte[] Encode(string text)
        {
            return new UTF8Encoding(false).GetBytes(text);
        }

        /// <summary>
        /// Refuses an href the CLR would not accept for a simply named assembly, or one that leaves the exe folder: empty,
        /// absolute or rooted (a drive, a leading slash), with "..", or with a backslash.
        /// </summary>
        public static void CheckHref(string href)
        {
            if (string.IsNullOrEmpty(href) || href.Contains(":") || href.StartsWith("/", StringComparison.Ordinal) || href.Contains("\\") ||
                href.Contains("..") || Path.IsPathRooted(href))
            {
                throw new ArgumentException("the codeBase href must be relative, use '/' and stay under the exe folder: " + href, nameof(href));
            }
        }

        public static bool IsOurs(string text)
        {
            return text != null && text.Contains(OwnerComment);
        }

        public static bool IsSpikeConfig(string text)
        {
            return text != null && text.Contains(SpikeOwnerComment);
        }

        /// <summary>
        /// The codeBases of a config (any writer's), in order; <see cref="FormatException"/> when it is not well-formed XML or
        /// a <c>&lt;dependentAssembly&gt;</c> lacks its identity or codeBase. Used to check our own file after writing it.
        /// </summary>
        public static List<PcCodeBase> Parse(string text)
        {
            var list = new List<PcCodeBase>();
            var doc = new XmlDocument { XmlResolver = null };
            try
            {
                using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                {
                    doc.Load(reader);
                }
            }
            catch (XmlException ex)
            {
                throw new FormatException("not a valid config: " + ex.Message, ex);
            }
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("b", "urn:schemas-microsoft-com:asm.v1");
            XmlNodeList nodes = doc.SelectNodes("/configuration/runtime/b:assemblyBinding/b:dependentAssembly", ns);
            foreach (XmlNode node in nodes)
            {
                XmlElement identity = node.SelectSingleNode("b:assemblyIdentity", ns) as XmlElement;
                XmlElement codeBase = node.SelectSingleNode("b:codeBase", ns) as XmlElement;
                if (identity == null || codeBase == null) throw new FormatException("a dependentAssembly has no assemblyIdentity or codeBase");
                Version version;
                if (!Version.TryParse(codeBase.GetAttribute("version"), out version)) throw new FormatException("codeBase without a valid version");
                list.Add(new PcCodeBase(identity.GetAttribute("name"), version, codeBase.GetAttribute("href")));
            }
            return list;
        }

        private static string Version4(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0)).ToString(4);
        }
    }
}
