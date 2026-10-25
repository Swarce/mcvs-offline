namespace McvsOffline.Server.Core
{
    /// <summary>Server version (also reported by GET / and the Server header).</summary>
    public static class McvsVersion
    {
        // The embedded build cannot carry AssemblyVersion attributes (trimmed mscorlib), so the version lives here.
        public const string Current = "0.1.0-dev";
    }

    /// <summary>Fixed facts about the game build we serve (APK 1.17.32). Configurable values live in Settings.</summary>
    public static class McvsConstants
    {
        /// <summary>Federation client id of the Google Play build (report 02 §0.6).</summary>
        public const string ClientId = "2620:66102:1.17.32:android:googleplay";

        /// <summary>Default Federation port: any non-80/443 port, so FormatUri never rewrites our URLs (ARCHITECTURE §2.2).</summary>
        public const int DefaultPort = 51780;

        /// <summary>LocalAnubis Finder port used by the game itself (Embedded mode); never bind it.</summary>
        public const int LocalAnubisFinderPort = 51750;

        /// <summary>Root key of the game's own section in the Seshat profile (verify-C3 §1).</summary>
        public const string GameProfileSelector = "_mcvs";

        /// <summary>The account id prefix used in credentials and paths: "fed_id:&lt;account&gt;".</summary>
        public const string FedIdPrefix = "fed_id:";
    }
}
