// Tool: patcher - owner group: EMBED (hosts/embedded, patcher/)
// Plan (ARCHITECTURE §2.3, verify-C7 §7): add McvsOffline.Server.dll to assemblies/, IL-patch
// NicePlaySDKDLL.OnInitialize -> McvsOffline.Bootstrap.Init(), delete libaot-*.dll.so, optional patches
// (geo, bottile, lan), zipalign + apksigner. Only ever operates on the player's own APK.
using System;

namespace McvsOffline.Patcher
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.Error.WriteLine("McvsOffline.Patcher: not implemented yet (EMBED group).");
            Console.Error.WriteLine("Usage (planned): McvsOffline.Patcher --apk <your mcvs.apk> --server <McvsOffline.Server.dll> --out <offline.apk>");
            return 2;
        }
    }
}
