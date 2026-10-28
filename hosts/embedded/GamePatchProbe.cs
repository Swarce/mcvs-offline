// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Reflection;
using McvsOffline.Server.Core;

namespace McvsOffline.Embedded
{
    /// <summary>
    /// What the patcher did to the client this process runs, read from the loaded <c>Game</c> assembly
    /// (docs/design/economy-collector.md §2.13). <c>XamarinMainActivity.OnCreate</c> calls <c>GameDll.OnInitialize</c> before
    /// <c>NicePlaySDKDLL.OnInitialize</c> runs the bootstrap, so Game.dll is loaded by then. The server gets the result as
    /// <c>ServerOptions.ClientCapabilities</c>; this assembly never references the game, so the check is by name.
    /// </summary>
    public static class GamePatchProbe
    {
        /// <summary>The game's assembly (Game.dll).</summary>
        public const string GameAssembly = "Game";

        /// <summary>The class the 'store' patch adds to Game.dll (patcher/Patches/StorePatch.cs, <c>SafetyType</c>).</summary>
        public const string StoreSafetyType = "Game.McvsStoreSafety";

        /// <summary>Probes the assemblies loaded in this process. Never throws: what cannot be found out is null (unknown).</summary>
        public static ClientCapabilities Detect(Log log = null)
        {
            Assembly[] loaded;
            try
            {
                loaded = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch (Exception ex)
            {
                Warn(log, ex);
                return new ClientCapabilities();
            }
            return Detect(loaded, log);
        }

        /// <summary><see cref="Detect(Log)"/> over a given list of assemblies (tests). Never throws.</summary>
        internal static ClientCapabilities Detect(IEnumerable<Assembly> loaded, Log log)
        {
            var result = new ClientCapabilities();
            try
            {
                Assembly game = null;
                foreach (Assembly assembly in loaded)
                {
                    if (assembly != null && assembly.GetName().Name == GameAssembly)
                    {
                        game = assembly;
                        break;
                    }
                }
                if (game == null)
                {
                    if (log != null) log.Info("no Game assembly is loaded: the client's patches are unknown");
                    return result;
                }
                result.StorePatch = game.GetType(StoreSafetyType, false, false) != null;
                if (log != null) log.Debug("Game.dll: 'store' patch " + (result.StorePatch == true ? "present" : "missing"));
            }
            catch (Exception ex)
            {
                result.StorePatch = null;
                Warn(log, ex);
            }
            return result;
        }

        private static void Warn(Log log, Exception ex)
        {
            try
            {
                if (log != null) log.Warn("the loaded Game.dll could not be probed: the client's patches are unknown", ex);
            }
            catch (Exception)
            {
            }
        }
    }
}
