using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Patcher.Tools;

namespace McvsOffline.Patcher.Signing
{
    /// <summary>Signs with Google's apksigner: v1 + v2 + v3, for minSdk 21 (passwords go through the environment).</summary>
    public static class ExternalApkSigner
    {
        public static void Sign(string apksigner, string input, string output, SigningKey key, string alias)
        {
            var args = new List<string>
            {
                "sign",
                "--ks", key.Path,
                "--ks-pass", "env:MCVS_KS_PASS",
                "--key-pass", "env:MCVS_KS_PASS",
                "--min-sdk-version", KnownBuild.MinSdkVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--v1-signing-enabled", "true",
                "--v2-signing-enabled", "true",
                "--v3-signing-enabled", "true",
                "--v4-signing-enabled", "false",
                "--out", output,
            };
            if (key.Certificate != null)
            {
                args.Insert(3, "--ks-type");
                args.Insert(4, "PKCS12");
            }
            if (!string.IsNullOrEmpty(alias))
            {
                args.Add("--ks-key-alias");
                args.Add(alias);
            }
            args.Add(input);
            var env = new Dictionary<string, string> { ["MCVS_KS_PASS"] = key.Password ?? "" };
            int code = BuildTools.Run(apksigner, args, env, out string text);
            if (code != 0 || !File.Exists(output)) throw new InvalidOperationException("apksigner sign failed (exit " + code + "):\n" + text);
        }

        /// <summary>Runs <c>apksigner verify</c>; returns its output, throws when verification fails.</summary>
        public static string Verify(string apksigner, string apk)
        {
            int code = BuildTools.Run(apksigner, new[] { "verify", "--verbose", "--min-sdk-version", KnownBuild.MinSdkVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), apk },
                                      null, out string text);
            if (code != 0) throw new InvalidOperationException("apksigner verify failed (exit " + code + "):\n" + text);
            return text;
        }
    }
}
