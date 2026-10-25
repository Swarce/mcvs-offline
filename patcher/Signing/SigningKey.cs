using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace McvsOffline.Patcher.Signing
{
    /// <summary>
    /// The player's signing key. Android only installs an update over an existing install when it is signed with the
    /// same key, and uninstalling deletes the game's folder (saves included), so the key must be kept: by default it
    /// is generated once into the user's profile and reused for every later patch.
    /// </summary>
    public sealed class SigningKey : IDisposable
    {
        public const string DefaultPassword = "mcvsoffline";
        public const string DefaultFileName = "mcvs-offline-signing.p12";

        private SigningKey(string path, string password, X509Certificate2 certificate)
        {
            Path = path;
            Password = password;
            Certificate = certificate;
        }

        public string Path { get; }

        public string Password { get; }

        /// <summary>Null for keystores only apksigner can read (e.g. JKS).</summary>
        public X509Certificate2 Certificate { get; }

        public RSA PrivateKey => Certificate?.GetRSAPrivateKey();

        /// <summary>Default location: %APPDATA%\McvsOffline (Windows) or ~/.config/McvsOffline (Linux/macOS).</summary>
        public static string DefaultPath()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(baseDir)) baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return System.IO.Path.Combine(baseDir, "McvsOffline", DefaultFileName);
        }

        /// <summary>Loads a PKCS#12 keystore; returns a key without certificate when the file is another format (JKS).</summary>
        public static SigningKey Load(string path, string password)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("keystore not found: " + path, path);
            try
            {
                var certificate = new X509Certificate2(File.ReadAllBytes(path), password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
                if (!certificate.HasPrivateKey) throw new InvalidDataException(path + " contains no private key");
                if (certificate.GetRSAPrivateKey() == null) throw new InvalidDataException(path + ": only RSA keys are supported by the built-in signer");
                return new SigningKey(path, password, certificate);
            }
            catch (CryptographicException)
            {
                // Not PKCS#12 (or wrong password): leave it to apksigner, which also reads JKS.
                return new SigningKey(path, password, null);
            }
        }

        /// <summary>Creates a self-signed RSA-2048 key valid for 30 years and saves it as PKCS#12.</summary>
        public static SigningKey Generate(string path, string password)
        {
            using (RSA rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest("CN=MCVS Offline player build, O=MCVS Offline", rsa, HashAlgorithmName.SHA256,
                                                     RSASignaturePadding.Pkcs1);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                using (X509Certificate2 generated = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(30)))
                {
                    byte[] pfx = generated.Export(X509ContentType.Pkcs12, password);
                    string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllBytes(path, pfx);
                }
            }
            return Load(path, password);
        }

        public void Dispose()
        {
            Certificate?.Dispose();
        }
    }
}
