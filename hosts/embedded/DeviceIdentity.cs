// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Embedded
{
    /// <summary>
    /// The stable <c>-DeviceId</c> passed in args.txt. Without it the client waits up to 10 s for the native Gameloft
    /// global device id, which needs Gameloft's servers, and then fails with "DeviceId timeout" (verify-C2 §1.2).
    /// The value is generated once and persisted in <c>McvsOffline/device-id.txt</c>; if that file is lost, the copy in
    /// the managed block of args.txt is adopted so the id does not change. Account ids do not depend on it (they are
    /// derived from the credential), so a new id is harmless, just untidy.
    /// </summary>
    public static class DeviceIdentity
    {
        public const string FileName = "device-id.txt";

        public static string LoadOrCreate(string directory, string fallbackFromArgs, Log log)
        {
            log = log ?? Log.Null;
            string path = Path.Combine(directory, FileName);
            try
            {
                string stored = AtomicFile.ReadAllTextOrNull(path);
                if (stored != null)
                {
                    stored = stored.Trim();
                    if (IsValid(stored)) return stored;
                    log.Warn(path + " does not hold a valid device id; replacing it");
                }
            }
            catch (Exception ex)
            {
                log.Warn("cannot read " + path, ex);
            }

            string id;
            if (IsValid(fallbackFromArgs))
            {
                id = fallbackFromArgs;
                log.Info("device id restored from args.txt: " + id);
            }
            else
            {
                id = Generate();
                log.Info("new device id: " + id);
            }
            try
            {
                AtomicFile.WriteAllText(path, id + "\n");
            }
            catch (Exception ex)
            {
                log.Warn("cannot persist the device id to " + path, ex);
            }
            return id;
        }

        /// <summary>"mcvs" + 32 lowercase hex digits.</summary>
        public static string Generate()
        {
            return "mcvs" + Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// 4 to 64 characters of [A-Za-z0-9_.]: no ':' (phantomArgs splits on it), no '-' (cmd_args and args.txt
        /// treat it as an argument start) and no whitespace.
        /// </summary>
        public static bool IsValid(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length < 4 || id.Length > 64) return false;
            foreach (char c in id)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.';
                if (!ok) return false;
            }
            return true;
        }
    }
}
