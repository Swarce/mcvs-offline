using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace McvsOffline.Server.Core.Accounts
{
    /// <summary>Random and derived identifiers (lower-case hex: safe in paths, form values, file names and cmd_args).</summary>
    public static class Ids
    {
        private static readonly object RngGate = new object();
        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        /// <summary><paramref name="bytes"/> cryptographically random bytes as 2*bytes hex characters.</summary>
        public static string RandomHex(int bytes)
        {
            var buffer = new byte[bytes];
            lock (RngGate) Rng.GetBytes(buffer);
            return ToHex(buffer);
        }

        /// <summary>Lower-case hex SHA-1 of the UTF-8 text.</summary>
        public static string Sha1Hex(string text)
        {
            using (var sha = SHA1.Create())
            {
                return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? "")));
            }
        }

        public static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
