using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace McvsOffline.Patcher.Manifest
{
    /// <summary>
    /// Makes the re-signed APK installable next to other Gameloft games.
    /// <para>
    /// The 1.17.32 manifest declares the vendor-wide signature permission
    /// <c>&lt;permission android:name="glshare.permission.ACCESS_SHARED_DATA" android:protectionLevel="signature"/&gt;</c>
    /// (decompiled-java resources/AndroidManifest.xml:19-22), which every Gameloft game declares for its
    /// <c>KeyProvider</c> data-sharing provider (<c>com.gameloft.android.DataSharing</c> queries the providers of every
    /// installed com.gameloft app). Since Android 5.0, PackageManager refuses a package that redeclares a permission
    /// owned by an installed package signed with another certificate (INSTALL_FAILED_DUPLICATE_PERMISSION), so an APK
    /// re-signed with the player's key would neither install next to Asphalt 9, MC5, N.O.V.A. 3... nor let them install
    /// afterwards. Every permission the manifest declares outside its own package is therefore renamed into the
    /// package's namespace. The pool string is shared by the <c>&lt;permission&gt;</c>, the <c>&lt;uses-permission&gt;</c>
    /// and the provider's <c>android:permission</c>, so all three change together and the provider stays protected.
    /// Nothing is lost: our own process reads its own provider without any permission, and other Gameloft apps' providers
    /// were already out of reach because the signature changed (DataSharing catches the SecurityException).
    /// </para>
    /// </summary>
    public static class ManifestPatch
    {
        public const string EntryName = "AndroidManifest.xml";
        public const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

        /// <summary>Infix of the renamed permissions: "&lt;package&gt;.mcvs.&lt;original name&gt;".</summary>
        public const string ScopeInfix = ".mcvs.";

        /// <summary>The package-scoped name that replaces <paramref name="permission"/>.</summary>
        public static string ScopedName(string package, string permission)
        {
            return package + ScopeInfix + permission;
        }

        /// <summary>
        /// Returns the manifest with every non-package-scoped declared permission renamed, or null when it declares none.
        /// Throws <see cref="InvalidDataException"/> for a manifest that is not binary XML, has no package, or uses such a
        /// permission's text for anything but a permission reference (renaming it there would change that too).
        /// </summary>
        public static byte[] Apply(byte[] manifest, ICollection<string> notes)
        {
            BinaryXml xml = BinaryXml.Parse(manifest);
            List<BinaryXml.Attribute> attributes = xml.StringAttributes();
            string package = attributes.FirstOrDefault(a => a.Element == "manifest" && a.Namespace.Length == 0 && a.Name == "package")?.Value;
            if (string.IsNullOrEmpty(package)) throw new InvalidDataException(EntryName + ": <manifest> has no package attribute");

            bool changed = false;
            foreach (BinaryXml.Attribute declared in DeclaredPermissions(attributes))
            {
                string name = declared.Value;
                if (IsPackageScoped(name, package)) continue;
                int index = declared.RawValue >= 0 ? declared.RawValue : declared.TypedString;
                if (declared.RawValue >= 0 && declared.TypedString >= 0 && declared.RawValue != declared.TypedString)
                    throw new InvalidDataException(EntryName + ": " + declared + " has different raw and typed values");

                List<BinaryXml.Attribute> uses = attributes.Where(a => a.RawValue == index || a.TypedString == index).ToList();
                List<BinaryXml.Attribute> unexpected = uses.Where(u => !IsPermissionReference(u)).ToList();
                if (unexpected.Count > 0)
                {
                    throw new InvalidDataException(EntryName + ": the text of permission " + name + " is also used by " +
                                                   string.Join(", ", unexpected) + "; refusing to rename it");
                }
                if (attributes.Any(a => a.Element == name || a.Name == name || a.Namespace == name) || index < xml.ResourceMapCount)
                    throw new InvalidDataException(EntryName + ": the text of permission " + name + " is also an element or attribute name");

                string scoped = ScopedName(package, name);
                xml.Replace(index, scoped);
                changed = true;
                notes?.Add("manifest: vendor-wide signature permission " + name + " renamed to " + scoped + " in " +
                           string.Join(", ", uses.Select(u => "<" + u.Element + " android:" + u.Name + ">")) +
                           " (another Gameloft game declaring it would block the install: INSTALL_FAILED_DUPLICATE_PERMISSION)");
            }
            return changed ? xml.ToArray() : null;
        }

        /// <summary>The android:name attributes of the manifest's &lt;permission&gt; elements.</summary>
        public static IEnumerable<BinaryXml.Attribute> DeclaredPermissions(IEnumerable<BinaryXml.Attribute> attributes)
        {
            return attributes.Where(a => a.Element == "permission" && a.Namespace == AndroidNamespace && a.Name == "name");
        }

        /// <summary>
        /// True when <paramref name="permission"/> lies in the package's own namespace, like the 1.17.32 C2D_MESSAGE
        /// permission: by convention no other app declares such a name, so no other installed app owns it.
        /// </summary>
        public static bool IsPackageScoped(string permission, string package)
        {
            return permission.StartsWith(package + ".", StringComparison.Ordinal);
        }

        private static bool IsPermissionReference(BinaryXml.Attribute a)
        {
            if (a.Namespace != AndroidNamespace) return false;
            switch (a.Name)
            {
                case "name":
                    return a.Element == "permission" || a.Element == "uses-permission" || a.Element == "uses-permission-sdk-23";
                case "permission":
                case "readPermission":
                case "writePermission":
                    return true;
                default:
                    return false;
            }
        }
    }
}
