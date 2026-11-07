using System;
using System.Collections.Generic;
using System.IO;

namespace McvsOffline.Tests
{
    /// <summary>
    /// The folders of the player's real client assemblies the real-client tests run against (docs/design/pc-port.md §4.7):
    /// the Android APK's assemblies (MCVS_REFS) and, when present, the Steam install (MCVS_PC_REFS). Never committed;
    /// read only. A real-client test is a <c>[Theory]</c> over <see cref="RealClientFolders"/> with the parameters
    /// <c>(string platform, string folder)</c>.
    /// </summary>
    public static class TestFolders
    {
        public const string Android = "android";
        public const string Pc = "pc";

        public const string DefaultAndroidRefs = "/home/user/mcvs-work/extracted/assemblies";
        public const string DefaultPcRefs = "/home/user/mcvs-work/pc/install";

        /// <summary>MCVS_REFS, default <see cref="DefaultAndroidRefs"/>.</summary>
        public static string AndroidRefs
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("MCVS_REFS");
                return string.IsNullOrEmpty(dir) ? DefaultAndroidRefs : dir;
            }
        }

        /// <summary>MCVS_PC_REFS, default <see cref="DefaultPcRefs"/>.</summary>
        public static string PcRefs
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("MCVS_PC_REFS");
                return string.IsNullOrEmpty(dir) ? DefaultPcRefs : dir;
            }
        }

        /// <summary>
        /// ("android", MCVS_REFS) always, and ("pc", MCVS_PC_REFS) when that folder has Gameloft.OnlinePlatform.dll or
        /// MCVS_REQUIRE_PC=1 (then a missing file fails the PC case instead of hiding it).
        /// </summary>
        public static IEnumerable<object[]> RealClientFolders()
        {
            yield return new object[] { Android, AndroidRefs };
            if (File.Exists(Path.Combine(PcRefs, "Gameloft.OnlinePlatform.dll")) || RequirePc)
                yield return new object[] { Pc, PcRefs };
        }

        /// <summary>MCVS_REQUIRE_PC=1 (scripts/build.sh sets it when MCVS_PC_REFS has the game's assemblies).</summary>
        public static bool RequirePc
        {
            get { return Environment.GetEnvironmentVariable("MCVS_REQUIRE_PC") == "1"; }
        }

        /// <summary>Whether a missing client file fails the test on <paramref name="platform"/> (instead of skipping it).</summary>
        public static bool Required(string platform)
        {
            return platform == Pc ? RequirePc : Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1";
        }

        /// <summary>The environment variable that names <paramref name="platform"/>'s folder (for messages).</summary>
        public static string VariableOf(string platform)
        {
            return platform == Pc ? "MCVS_PC_REFS" : "MCVS_REFS";
        }
    }
}
