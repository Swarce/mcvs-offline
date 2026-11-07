// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLLs and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace McvsOffline.Embedded
{
    /// <summary>
    /// The game's types by name from assemblies that are ALREADY loaded: our code never makes the runtime load a game assembly
    /// (docs/design/pc-port.md §8.9). On PC, EngineBridge, Gameloft.Engine, Gameloft.Util and Game are redirected to
    /// McvsOffline\bin by the .exe.config codeBase, which applies to full references only: a partial name such as the "Game" of
    /// <c>Type.GetType("Game.X, Game")</c> gets no application-config policy, so the runtime probes the exe folder and loads the
    /// STOCK file. Build 17 did exactly that at boot (GameReflection.Load, before the engine's own full-name load of the gameload
    /// patch), and the engine then got the stock Game back. Lookups here only compare simple names; nothing throws.
    /// </summary>
    internal static class LoadedTypes
    {
        public const string GameAssembly = "Game";
        public const string EngineAssembly = "Gameloft.Engine";

        /// <summary>What is loaded instead of AppDomain.CurrentDomain.GetAssemblies() (tests: one sandbox's assemblies).</summary>
        internal static Func<IEnumerable<Assembly>> Source { get; set; }

        /// <summary>The loaded assembly whose simple name is <paramref name="assemblyName"/> (the first one), or null.</summary>
        public static Assembly Find(string assemblyName)
        {
            try
            {
                Func<IEnumerable<Assembly>> source = Source;
                IEnumerable<Assembly> loaded = source != null ? source() : AppDomain.CurrentDomain.GetAssemblies();
                if (loaded == null) return null;
                foreach (Assembly a in loaded)
                {
                    if (a != null && string.Equals(a.GetName().Name, assemblyName, StringComparison.Ordinal)) return a;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        /// <summary><paramref name="typeName"/> (full name) from the loaded assembly <paramref name="assemblyName"/>, or null when it is not loaded.</summary>
        public static Type Find(string assemblyName, string typeName)
        {
            try
            {
                Assembly a = Find(assemblyName);
                return a == null ? null : a.GetType(typeName, false, false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// A Gameloft.Engine type for a caller that ran before this class (watchdog, graphics, threading line, exit): the loaded one;
        /// on Android, when Gameloft.Engine is not loaded yet, <c>Type.GetType</c> by its partial name as before (the APK has one
        /// copy and no redirect). On PC never a load: Gameloft.Engine is loaded before the bootstrap runs (EngineBridge.Class1.Start,
        /// which calls it, references the engine statically, so the JIT binds it through the codeBase first), and null there means
        /// it really is not loaded.
        /// </summary>
        public static Type EngineType(string typeName)
        {
            Type type = Find(EngineAssembly, typeName);
#if !MCVS_WINDOWS
            if (type == null)
            {
                try
                {
                    type = Type.GetType(typeName + ", " + EngineAssembly, false);
                }
                catch (Exception)
                {
                }
            }
#endif
            return type;
        }
    }
}
