using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace McvsOffline.PcSpike
{
    /// <summary>
    /// The game's crash logs on PC: <c>Gameloft.SaveLogs.LogToFile("crashLog", e)</c> (called from <c>Log</c> for unhandled
    /// exceptions) writes <c>%LOCALAPPDATA%\Gameloft\crashLog_&lt;date&gt;.log</c>, one line per field, each line a separately
    /// AES-encrypted (CBC, PKCS7, 192-bit key) base64 string. The key and IV are derived in <c>SaveLogs.GetKey/GetIV</c> from a
    /// seed byte and a table of increments; the same derivation is repeated here so 'collect' adds a readable copy.
    /// </summary>
    public static class CrashLogDecoder
    {
        private static readonly byte[] KeySteps =
        {
            0, 38, 212, 239, 22, 0, 243, 43, 204, 0, 4, 55, 206, 9, 14, 36, 189, 5, 1, 66, 213, 40, 199, 0, 0,
        };

        private static readonly byte[] IvSteps =
        {
            0, 35, 242, 229, 39, 241, 245, 227, 49, 2, 6, 6, 216, 40, 236, 237, 1, 247, 231,
        };

        public static byte[] Key() => Derive(71, KeySteps, 24);

        public static byte[] Iv() => Derive(79, IvSteps, 16);

        private static byte[] Derive(byte seed, byte[] steps, int length)
        {
            var result = new byte[length];
            result[0] = seed;
            for (int i = 1; i < length; i++) result[i] = (byte)(result[i - 1] + steps[i]);
            return result;
        }

        /// <summary>Decrypts one line; returns it unchanged when it is not one of these encrypted lines.</summary>
        public static string DecryptLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return line;
            try
            {
                byte[] cipher = Convert.FromBase64String(line.Trim());
                using (Aes aes = Aes.Create())
                {
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = Key();
                    aes.IV = Iv();
                    byte[] plain = aes.DecryptCbc(cipher, aes.IV, PaddingMode.PKCS7);
                    return Encoding.UTF8.GetString(plain);
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is CryptographicException)
            {
                return line;
            }
        }

        public static string DecryptFile(string path)
        {
            var sb = new StringBuilder();
            foreach (string line in File.ReadLines(path)) sb.Append(DecryptLine(line)).Append("\r\n");
            return sb.ToString();
        }

        /// <summary>The game's own encryption, for the tests (StreamWriter.Write of a UTF-8 string, as SaveLogs.EncryptAES does).</summary>
        internal static string EncryptLine(string text)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Key = Key();
                aes.IV = Iv();
                return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(text), aes.IV, PaddingMode.PKCS7));
            }
        }
    }
}
