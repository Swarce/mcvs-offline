using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Android;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The one JNI table of the settings page (design §8.6, <see cref="AndroidJni"/>): every framework entry exists with that
    /// descriptor and static-ness in android.jar (API 29: MCVS_ANDROID_JAR, or platforms/android-29/android.jar next to the
    /// build tools; a small class-file reader), and every game entry (com/gameloft/...) in the APK's classes*.dex (dexdump
    /// from the build tools). Skipped when those are absent, required with MCVS_REQUIRE_REAL_CLIENT=1. On the phone a miss
    /// would only turn a capability off (Status › host › missingJni); here it fails the build.
    /// </summary>
    public class JniTableTests
    {
        private static readonly string[] Uses =
        {
            AndroidJni.Common, AndroidJni.Browser, AndroidJni.Post, AndroidJni.Downloads, AndroidJni.Send, AndroidJni.FileUri,
            AndroidJni.StrictModeUse, AndroidJni.Clipboard, AndroidJni.Toast, AndroidJni.ShareTextUse, AndroidJni.ExitGame,
            AndroidJni.ExitHost, AndroidJni.Scan, AndroidJni.Storage, AndroidJni.Notify,
        };

        private readonly ITestOutputHelper output;

        public JniTableTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static bool Required
        {
            get { return Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1"; }
        }

        [Fact]
        public void TheTableIsWellFormed()
        {
            Assert.Equal(AndroidJni.Table.Length, AndroidJni.Table.Select(m => m.Key).Distinct().Count());
            // Every public static entry of the class is in the table (nothing is called past it).
            var declared = typeof(AndroidJni).GetFields().Where(f => f.FieldType == typeof(JniMember)).Select(f => (JniMember)f.GetValue(null)).ToList();
            Assert.Equal(declared.Count, AndroidJni.Table.Length);
            Assert.All(declared, m => Assert.Contains(m, AndroidJni.Table));
            foreach (JniMember m in AndroidJni.Table)
            {
                Assert.Contains(m.Use, Uses);
                Assert.DoesNotContain('.', m.Class);
                if (m.IsField)
                {
                    // Static fields, and the notification group's instance fields (ApplicationInfo, PackageInfo).
                    Assert.True(m.IsStatic || m.Use == AndroidJni.Notify, m.Key);
                    Assert.False(m.Descriptor.StartsWith("(", StringComparison.Ordinal), m.Key);
                    Assert.NotNull(JavaType(m.Descriptor, out int end));
                }
                else
                {
                    Assert.NotNull(JavaSignature(m.Descriptor));
                    if (m.Name == "<init>") Assert.False(m.IsStatic);
                }
            }
            Assert.Equal(29, AndroidJni.DownloadsUri.MinSdk);
            Assert.Equal("android/content/Intent.addFlags(I)Landroid/content/Intent;", AndroidJni.IntentAddFlags.Key);
            Assert.Equal("com/gameloft/ingamebrowser/InGameBrowser.b:Z", AndroidJni.IgbShown.Key);
            // The notification group is resolved on Android 13+ only (nothing goes missing on older phones).
            Assert.All(AndroidJni.Table.Where(m => m.Use == AndroidJni.Notify), m => Assert.Equal(AndroidJni.Tiramisu, m.MinSdk));
            Assert.Equal("android/content/pm/ApplicationInfo.targetSdkVersion:I", AndroidJni.AppInfoTargetSdk.Key);
            Assert.True(AndroidJni.Available(33, new List<string>(), AndroidJni.Notify));
            Assert.False(AndroidJni.Available(32, new List<string>(), AndroidJni.Notify));
        }

        /// <summary>
        /// The constants the notification request passes (AndroidJni.PostNotifications, PermissionGranted, GetPermissions,
        /// Tiramisu) are android.jar's own, from an API 33+ jar: MCVS_ANDROID_JAR_33, or platforms/android-NN (NN &gt;= 33) next to
        /// the API 29 jar. Skipped when none is installed (only API 29 is required by the build).
        /// </summary>
        [Fact]
        public void NotificationConstantsMatchAnApi33AndroidJar()
        {
            string jar = AndroidJar33();
            if (jar == null)
            {
                output.WriteLine("no android.jar of API 33 or later (MCVS_ANDROID_JAR_33, platforms/android-33+): skipped");
                return;
            }
            output.WriteLine(jar);
            using (ZipArchive zip = ZipFile.OpenRead(jar))
            {
                Func<string, ClassFile> load = name =>
                {
                    ZipArchiveEntry entry = zip.GetEntry(name + ".class");
                    if (entry == null) return null;
                    using (Stream s = entry.Open())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return ClassFile.Parse(ms.ToArray());
                    }
                };
                Assert.Equal(AndroidJni.PostNotifications, load("android/Manifest$permission").Fields.Single(f => f.Name == "POST_NOTIFICATIONS").Constant);
                Assert.Equal(AndroidJni.Tiramisu, load("android/os/Build$VERSION_CODES").Fields.Single(f => f.Name == "TIRAMISU").Constant);
                ClassFile pm = load("android/content/pm/PackageManager");
                Assert.Equal(AndroidJni.PermissionGranted, pm.Fields.Single(f => f.Name == "PERMISSION_GRANTED").Constant);
                Assert.Equal(AndroidJni.GetPermissions, pm.Fields.Single(f => f.Name == "GET_PERMISSIONS").Constant);
                // And every notification entry also exists there with the same static-ness.
                foreach (JniMember m in AndroidJni.Table.Where(x => x.Use == AndroidJni.Notify))
                {
                    ClassFile.Member found = Find(load, m.Class, m.Name, m.Descriptor, m.IsField);
                    Assert.True(found != null && found.IsStatic == m.IsStatic && found.IsPublic, m.Key);
                }
            }
        }

        private static string AndroidJar33()
        {
            string jar = Environment.GetEnvironmentVariable("MCVS_ANDROID_JAR_33");
            if (!string.IsNullOrEmpty(jar) && File.Exists(jar)) return jar;
            string jar29 = AndroidJar();
            string platforms = jar29 == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(jar29));
            if (platforms == null || !Directory.Exists(platforms)) return null;
            return Directory.GetDirectories(platforms, "android-*")
                            .Select(d => new { Dir = d, Api = int.TryParse(Path.GetFileName(d).Substring("android-".Length), out int api) ? api : 0 })
                            .Where(x => x.Api >= 33 && File.Exists(Path.Combine(x.Dir, "android.jar")))
                            .OrderBy(x => x.Api).Select(x => Path.Combine(x.Dir, "android.jar")).FirstOrDefault();
        }

        [Fact]
        public void CapabilitiesNeedEveryMemberOfTheirGroups()
        {
            var none = new List<string>();
            Assert.True(AndroidJni.Available(34, none, AndroidJni.ShareFileUses(34)));
            Assert.True(AndroidJni.Available(28, none, AndroidJni.ShareFileUses(28)));
            Assert.Equal(new[] { AndroidJni.Common, AndroidJni.Downloads, AndroidJni.Send }, AndroidJni.ShareFileUses(29));
            Assert.Equal(new[] { AndroidJni.Common, AndroidJni.Send, AndroidJni.FileUri, AndroidJni.StrictModeUse }, AndroidJni.ShareFileUses(24));
            Assert.Equal(new[] { AndroidJni.Common, AndroidJni.Send, AndroidJni.FileUri }, AndroidJni.ShareFileUses(21));
            Assert.False(AndroidJni.Available(34, new List<string> { AndroidJni.ResolverInsert.Key }, AndroidJni.Downloads));
            // An entry above the device's SDK is not resolved and does not count as missing; a group with no entry left is off.
            Assert.True(AndroidJni.Available(28, new List<string> { AndroidJni.DownloadsUri.Key }, AndroidJni.Downloads));
            Assert.False(AndroidJni.Available(34, none, "no-such-group"));
            Assert.False(AndroidJni.Available(34, new List<string> { AndroidJni.GetActivity.Key }, AndroidJni.Common, AndroidJni.Toast));
            Assert.True(AndroidJni.Available(34, new List<string> { AndroidJni.GetActivity.Key }, AndroidJni.Toast));
        }

        [Fact]
        public void FrameworkEntriesExistInAndroidJar()
        {
            string jar = AndroidJar();
            if (jar == null)
            {
                Assert.False(Required, "MCVS_REQUIRE_REAL_CLIENT=1 but android.jar (API 29) was not found: set MCVS_ANDROID_JAR");
                output.WriteLine("android.jar not found: skipped");
                return;
            }
            output.WriteLine(jar);
            var problems = new List<string>();
            int checkedCount = 0;
            using (ZipArchive zip = ZipFile.OpenRead(jar))
            {
                var classes = new Dictionary<string, ClassFile>(StringComparer.Ordinal);
                Func<string, ClassFile> load = name =>
                {
                    if (classes.TryGetValue(name, out ClassFile cached)) return cached;
                    ZipArchiveEntry entry = zip.GetEntry(name + ".class");
                    ClassFile parsed = null;
                    if (entry != null)
                    {
                        using (Stream s = entry.Open())
                        using (var ms = new MemoryStream())
                        {
                            s.CopyTo(ms);
                            parsed = ClassFile.Parse(ms.ToArray());
                        }
                    }
                    classes[name] = parsed;
                    return parsed;
                };
                foreach (JniMember m in AndroidJni.Table.Where(x => !x.Class.StartsWith("com/gameloft/", StringComparison.Ordinal)))
                {
                    checkedCount++;
                    ClassFile.Member found = Find(load, m.Class, m.Name, m.Descriptor, m.IsField);
                    if (found == null) problems.Add("not found: " + m.Key);
                    else if (found.IsStatic != m.IsStatic) problems.Add((m.IsStatic ? "not static: " : "static: ") + m.Key);
                    else if (!found.IsPublic) problems.Add("not public: " + m.Key);
                }
            }
            output.WriteLine(checkedCount + " framework entries checked, " + problems.Count + " problems");
            foreach (string p in problems) output.WriteLine("  " + p);
            Assert.Empty(problems);
            Assert.True(checkedCount >= 40);
        }

        [Fact]
        public void GameEntriesExistInTheApksDex()
        {
            string apk = TestEnvironment.GameApk;
            string dexdump = TestEnvironment.BuildToolsDir == null ? null : Path.Combine(TestEnvironment.BuildToolsDir, "dexdump");
            if (apk == null || dexdump == null || !File.Exists(dexdump))
            {
                Assert.False(Required, "MCVS_REQUIRE_REAL_CLIENT=1 but the APK (MCVS_APK) or dexdump (MCVS_BUILD_TOOLS) is missing");
                output.WriteLine("APK or dexdump not available: skipped");
                return;
            }
            string dir = TestEnvironment.NewTempDir("dex");
            var members = new HashSet<string>(StringComparer.Ordinal);
            using (ZipArchive zip = ZipFile.OpenRead(apk))
            {
                foreach (ZipArchiveEntry entry in zip.Entries.Where(e => e.FullName.StartsWith("classes", StringComparison.Ordinal) && e.FullName.EndsWith(".dex", StringComparison.Ordinal)))
                {
                    string path = Path.Combine(dir, entry.FullName);
                    entry.ExtractToFile(path, true);
                    foreach (string member in DexMembers(dexdump, path)) members.Add(member);
                }
            }
            output.WriteLine(members.Count + " members in the APK's dex");
            Assert.True(members.Count > 10000);
            var problems = new List<string>();
            int checkedCount = 0;
            foreach (JniMember m in AndroidJni.Table.Where(x => x.Class.StartsWith("com/gameloft/", StringComparison.Ordinal)))
            {
                checkedCount++;
                string cls = m.Class.Replace('/', '.');
                string signature = m.IsField ? JavaType(m.Descriptor, out int end) : JavaSignature(m.Descriptor);
                string key = cls + "|" + m.Name + "|" + (m.IsStatic ? "static" : "instance") + "|public|" + signature;
                if (!members.Contains(key)) problems.Add(m.Key + " (" + key + ")");
            }
            output.WriteLine(checkedCount + " game entries checked");
            foreach (string p in problems) output.WriteLine("  missing: " + p);
            Assert.Empty(problems);
            Assert.Equal(9, checkedCount);
        }

        // ------------------------------------------------------------------ descriptors

        /// <summary>"(Ljava/lang/String;Z)V" → "(java.lang.String,boolean)void" (dexdump's XML spelling).</summary>
        private static string JavaSignature(string descriptor)
        {
            if (!descriptor.StartsWith("(", StringComparison.Ordinal)) return null;
            int close = descriptor.IndexOf(')');
            if (close < 0) return null;
            var parameters = new List<string>();
            int i = 1;
            while (i < close)
            {
                string t = JavaType(descriptor.Substring(i, close - i), out int used);
                if (t == null) return null;
                parameters.Add(t);
                i += used;
            }
            string ret = JavaType(descriptor.Substring(close + 1), out int retUsed);
            if (ret == null || close + 1 + retUsed != descriptor.Length) return null;
            return "(" + string.Join(",", parameters) + ")" + ret;
        }

        private static string JavaType(string descriptor, out int used)
        {
            used = 0;
            if (descriptor.Length == 0) return null;
            switch (descriptor[0])
            {
                case 'Z': used = 1; return "boolean";
                case 'B': used = 1; return "byte";
                case 'C': used = 1; return "char";
                case 'S': used = 1; return "short";
                case 'I': used = 1; return "int";
                case 'J': used = 1; return "long";
                case 'F': used = 1; return "float";
                case 'D': used = 1; return "double";
                case 'V': used = 1; return "void";
                case '[':
                    string element = JavaType(descriptor.Substring(1), out int inner);
                    if (element == null) return null;
                    used = inner + 1;
                    return element + "[]";
                case 'L':
                    int semi = descriptor.IndexOf(';');
                    if (semi < 2) return null;
                    used = semi + 1;
                    return descriptor.Substring(1, semi - 1).Replace('/', '.').Replace('$', '.');
                default:
                    return null;
            }
        }

        // ------------------------------------------------------------------ android.jar

        private static string AndroidJar()
        {
            string jar = Environment.GetEnvironmentVariable("MCVS_ANDROID_JAR");
            if (!string.IsNullOrEmpty(jar) && File.Exists(jar)) return jar;
            string tools = TestEnvironment.BuildToolsDir;
            if (tools == null) return null;
            string sdk = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(tools)));
            jar = Path.Combine(sdk, "platforms", "android-29", "android.jar");
            return File.Exists(jar) ? jar : null;
        }

        /// <summary>The member in the class or (as JNI's Get*ID does) in a superclass or a super-interface.</summary>
        private static ClassFile.Member Find(Func<string, ClassFile> load, string className, string name, string descriptor, bool field)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(className);
            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (current == null || !seen.Add(current)) continue;
                ClassFile cls = load(current);
                if (cls == null) continue;
                ClassFile.Member m = (field ? cls.Fields : cls.Methods).FirstOrDefault(x => x.Name == name && x.Descriptor == descriptor);
                if (m != null) return m;
                if (name == "<init>") return null;   // constructors are not inherited
                queue.Enqueue(cls.SuperName);
                foreach (string i in cls.Interfaces) queue.Enqueue(i);
            }
            return null;
        }

        /// <summary>The parts of a Java class file the check needs: names, super types, fields and methods with access flags.</summary>
        private sealed class ClassFile
        {
            public sealed class Member
            {
                public string Name;
                public string Descriptor;
                public int Access;
                public bool IsStatic { get { return (Access & 0x0008) != 0; } }
                public bool IsPublic { get { return (Access & 0x0001) != 0; } }

                /// <summary>A field's ConstantValue (string or int), or null.</summary>
                public object Constant;
            }

            public string Name;
            public string SuperName;
            public List<string> Interfaces = new List<string>();
            public List<Member> Fields = new List<Member>();
            public List<Member> Methods = new List<Member>();

            public static ClassFile Parse(byte[] data)
            {
                int p = 0;
                Func<int> u1 = () => data[p++];
                Func<int> u2 = () => { int v = (data[p] << 8) | data[p + 1]; p += 2; return v; };
                Func<long> u4 = () => { long v = ((long)data[p] << 24) | ((long)data[p + 1] << 16) | ((long)data[p + 2] << 8) | data[p + 3]; p += 4; return v; };
                if (u4() != 0xCAFEBABE) throw new InvalidDataException("not a class file");
                u2();
                u2();
                int count = u2();
                var utf8 = new string[count];
                var classIndex = new int[count];
                var stringIndex = new int[count];
                var ints = new int?[count];
                for (int i = 1; i < count; i++)
                {
                    int tag = u1();
                    switch (tag)
                    {
                        case 1:
                            int length = u2();
                            utf8[i] = Encoding.UTF8.GetString(data, p, length);
                            p += length;
                            break;
                        case 7: classIndex[i] = u2(); break;
                        case 8: stringIndex[i] = u2(); break;
                        case 16: case 19: case 20: u2(); break;
                        case 3: ints[i] = (int)u4(); break;
                        case 4: case 9: case 10: case 11: case 12: case 17: case 18: p += 4; break;
                        case 5: case 6: p += 8; i++; break;
                        case 15: p += 3; break;
                        default: throw new InvalidDataException("constant pool tag " + tag);
                    }
                }
                Func<int, string> className = index => index == 0 ? null : utf8[classIndex[index]];
                var cls = new ClassFile();
                u2();
                cls.Name = className(u2());
                cls.SuperName = className(u2());
                int interfaces = u2();
                for (int i = 0; i < interfaces; i++) cls.Interfaces.Add(className(u2()));
                for (int kind = 0; kind < 2; kind++)
                {
                    int members = u2();
                    for (int i = 0; i < members; i++)
                    {
                        var m = new Member { Access = u2() };
                        m.Name = utf8[u2()];
                        m.Descriptor = utf8[u2()];
                        int attributes = u2();
                        for (int a = 0; a < attributes; a++)
                        {
                            string attribute = utf8[u2()];
                            int length = (int)u4();   // read before adding: u4 moves p itself
                            if (kind == 0 && attribute == "ConstantValue" && length == 2)
                            {
                                int index = (data[p] << 8) | data[p + 1];
                                m.Constant = stringIndex[index] != 0 ? utf8[stringIndex[index]] : (object)ints[index];
                            }
                            p += length;
                        }
                        (kind == 0 ? cls.Fields : cls.Methods).Add(m);
                    }
                }
                return cls;
            }
        }

        // ------------------------------------------------------------------ the APK's dex

        /// <summary>
        /// "class|name|static|visibility|signature" of every field, method and constructor in dexdump's XML layout
        /// (<c>dexdump -l xml</c>): fields "type", methods "(param,...)return", constructors "&lt;init&gt;" and "void".
        /// </summary>
        private static IEnumerable<string> DexMembers(string dexdump, string dex)
        {
            var psi = new ProcessStartInfo(dexdump)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add("xml");
            psi.ArgumentList.Add(dex);
            var result = new List<string>();
            using (Process process = Process.Start(psi))
            {
                process.ErrorDataReceived += (s, e) => { };
                process.BeginErrorReadLine();
                string package = null, cls = null, kind = null, name = null;
                Dictionary<string, string> attributes = null;
                List<string> parameters = null;
                bool inHeader = false;
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    if (line.StartsWith("<package name=\"", StringComparison.Ordinal)) package = Quoted(line);
                    else if (line.StartsWith("<class name=\"", StringComparison.Ordinal) || line.StartsWith("<interface name=\"", StringComparison.Ordinal)) cls = package + "." + Quoted(line);
                    else if (line.StartsWith("<field name=\"", StringComparison.Ordinal) || line.StartsWith("<method name=\"", StringComparison.Ordinal) ||
                             line.StartsWith("<constructor name=\"", StringComparison.Ordinal))
                    {
                        kind = line.Substring(1, line.IndexOf(' ') - 1);
                        name = kind == "constructor" ? "<init>" : Quoted(line);
                        attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                        parameters = new List<string>();
                        inHeader = true;
                    }
                    else if (inHeader && line == ">")
                    {
                        inHeader = false;
                        if (kind == "field") result.Add(Key(cls, name, attributes, attributes.TryGetValue("type", out string t) ? t : "?"));
                    }
                    else if (inHeader && line.StartsWith(" ", StringComparison.Ordinal) && line.Contains("=\""))
                    {
                        string trimmed = line.Trim();
                        int eq = trimmed.IndexOf('=');
                        attributes[trimmed.Substring(0, eq)] = Quoted(trimmed);
                    }
                    else if (line.StartsWith("<parameter ", StringComparison.Ordinal) && parameters != null)
                    {
                        int at = line.IndexOf("type=\"", StringComparison.Ordinal);
                        parameters.Add(Quoted(line.Substring(at)));
                    }
                    else if ((line == "</method>" || line == "</constructor>") && kind != null)
                    {
                        string ret = kind == "constructor" ? "void" : (attributes.TryGetValue("return", out string r) ? r : "?");
                        result.Add(Key(cls, name, attributes, "(" + string.Join(",", parameters) + ")" + ret));
                        kind = null;
                    }
                }
                process.WaitForExit();
            }
            return result;
        }

        private static string Key(string cls, string name, Dictionary<string, string> attributes, string signature)
        {
            bool isStatic = attributes.TryGetValue("static", out string s) && s == "true";
            string visibility = attributes.TryGetValue("visibility", out string v) ? v : "?";
            return cls + "|" + name + "|" + (isStatic ? "static" : "instance") + "|" + visibility + "|" + signature;
        }

        /// <summary>The first "..." value of a line.</summary>
        private static string Quoted(string line)
        {
            int start = line.IndexOf('"');
            int end = line.IndexOf('"', start + 1);
            return start < 0 || end < 0 ? "" : line.Substring(start + 1, end - start - 1);
        }
    }
}
