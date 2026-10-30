using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using McvsOffline.Embedded;

namespace McvsOffline.Embed.Tests.Support
{
    /// <summary>
    /// The host's <see cref="LoadedTypes"/> looks at every assembly of the AppDomain, as on the player's PC (one load context).
    /// This process also holds other tests' sandboxes, among them look-alike and real Gameloft.Engine/Game assemblies (the
    /// player's own, from MCVS_REFS and MCVS_PC_REFS); the host code compiled into the tests sees only the default context, so
    /// no test binds another test's engine and runs its native code. GameLoadOrderTests points its own sandboxed copy elsewhere.
    /// </summary>
    internal static class LoadedTypesIsolation
    {
#pragma warning disable CA2255 // the test assembly's own initializer, not a library's
        [ModuleInitializer]
        internal static void Initialize()
        {
            LoadedTypes.Source = () => AppDomain.CurrentDomain.GetAssemblies().Where(a => AssemblyLoadContext.GetLoadContext(a) == AssemblyLoadContext.Default);
        }
#pragma warning restore CA2255
    }
}
