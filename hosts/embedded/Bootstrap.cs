// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Compiled only into McvsOffline.Server.Embedded.csproj (the DLL that runs inside the game process).
// Protocol/wiring: ARCHITECTURE §2.3 "Embedded wiring"; verify-C7 §5-§7; verify-C1 (args.txt format).
using System;
using System.Threading;

namespace McvsOffline
{
    /// <summary>
    /// Entry point of the offline server inside the game. The patcher rewrites
    /// <c>Gameloft.NicePlaySDK.NicePlaySDKDLL.OnInitialize</c> (invoked reflectively by
    /// <c>XamarinMainActivity.OnCreate</c> before <c>MainLoop.Start</c>, Java GameInstaller and native init) into
    /// <c>call McvsOffline.Bootstrap::Init()</c> (verify-C7 §5).
    /// <para>Contract: idempotent, returns quickly, and never throws into the game's OnCreate.</para>
    /// </summary>
    public static class Bootstrap
    {
        private static int initialized;

        public static void Init()
        {
            if (Interlocked.Exchange(ref initialized, 1) != 0) return;
            try
            {
                // TODO(EMBED):
                //  1. Resolve <GlfRootPath> (the folder holding Data/ and args.txt, verify-C1 §1.1).
                //  2. Create McvsOffline.Server.McvsServer with DataDir = <GlfRootPath>/McvsOffline and
                //     DataPackDir = <GlfRootPath>/Data, and start it on a background thread.
                //  3. Write/merge the managed block of <GlfRootPath>/args.txt (ARCHITECTURE §2.2) before native reads it.
            }
            catch (Exception)
            {
                // Never let a server problem prevent the game from starting.
            }
        }
    }
}
