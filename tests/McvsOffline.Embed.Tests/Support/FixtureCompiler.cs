using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace McvsOffline.Embed.Tests.Support
{
    /// <summary>
    /// Compiles small look-alikes of the game assemblies with Roslyn (Release, like the game was built), so the patch
    /// locators see the same compiler-generated IL shapes as in the real 1.17.32 assemblies, and the patched code can be
    /// executed on net8 to check the patches behave.
    /// </summary>
    public static class FixtureCompiler
    {
        private static readonly Lazy<List<MetadataReference>> Framework = new Lazy<List<MetadataReference>>(() =>
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
                .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToList());

        public static byte[] Compile(string assemblyName, params string[] sources)
        {
            return Compile(assemblyName, sources, new byte[0][]);
        }

        public static byte[] Compile(string assemblyName, string[] sources, byte[][] references)
        {
            var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.CSharp7_3))).ToList();
            var refs = new List<MetadataReference>(Framework.Value);
            refs.AddRange(references.Select(r => (MetadataReference)MetadataReference.CreateFromImage(r)));
            var compilation = CSharpCompilation.Create(assemblyName, trees, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            using (var stream = new MemoryStream())
            {
                var result = compilation.Emit(stream);
                if (!result.Success)
                {
                    throw new InvalidOperationException("fixture " + assemblyName + " does not compile:\n" +
                                                        string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
                }
                return stream.ToArray();
            }
        }

        public static string RepoSource(params string[] relative)
        {
            return File.ReadAllText(Path.Combine(new[] { TestEnvironment.RepoRoot }.Concat(relative).ToArray()));
        }

        /// <summary>Loads assemblies from bytes into an isolated, collectible context.</summary>
        public sealed class Sandbox : AssemblyLoadContext, IDisposable
        {
            private readonly Dictionary<string, byte[]> images = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            public Sandbox() : base("fixture-" + Guid.NewGuid().ToString("N"), true) { }

            public Sandbox With(string name, byte[] image)
            {
                images[name] = image;
                return this;
            }

            public Assembly Get(string name)
            {
                return LoadFromAssemblyName(new AssemblyName(name));
            }

            protected override Assembly Load(AssemblyName name)
            {
                byte[] image;
                return images.TryGetValue(name.Name, out image) ? LoadFromStream(new MemoryStream(image)) : null;
            }

            public void Dispose()
            {
                Unload();
            }
        }
    }
}
