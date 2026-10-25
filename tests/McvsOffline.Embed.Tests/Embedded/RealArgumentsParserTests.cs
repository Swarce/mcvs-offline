using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using McvsOffline.Embedded;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Server.Core;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The args.txt the embedded host writes, through the native flattening (<see cref="GameArgsSimulator.Native"/>,
    /// verify-C1 §1.2) and then the game's REAL managed parser: <c>GameApplication.Arguments.FromString</c> from the
    /// player's Gameloft.Engine.dll (MCVS_REFS), which is what <c>MainLoop.OnStarted</c> calls. Skipped when the
    /// assembly is absent, unless MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    public class RealArgumentsParserTests
    {
        private readonly ITestOutputHelper output;

        public RealArgumentsParserTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheManagedBlockParsesIntoTheEmbeddedBootArguments(bool lanMode)
        {
            Type arguments = LoadArgumentsType();
            if (arguments == null) return;

            var settings = new Settings();
            settings.Override("lanMode", lanMode);
            string deviceId = "mcvs" + new string('a', 32);
            // A player's existing file (CRLF, one line of their own) plus our block.
            byte[] existing = Encoding.ASCII.GetBytes("-LogCategoryFilter Online\r\n");
            byte[] file = ArgsFile.Merge(existing, ManagedArgs.Build(settings, deviceId, 51780, null)).Content;

            object parsed = arguments.GetMethod("FromString").Invoke(null, new object[] { GameArgsSimulator.Native(file, "<Exe>/../..") });
            Assert.Equal("Embedded", Field(parsed, "ServerLocation").ToString()); // case-sensitive Enum.Parse
            Assert.Equal("QuickLaunch", Field(parsed, "AnubisCommand").ToString());
            Assert.Equal("http://127.0.0.1:51780/", Field(parsed, "EnvironmentSwitcherUrl"));
            Assert.Equal(Settings.DefaultSuperCredential, Field(parsed, "SuperCredential"));
            Assert.Equal(Settings.DefaultSuperPassword, Field(parsed, "SuperPassword"));
            Assert.Equal(deviceId, Field(parsed, "DeviceId"));
            Assert.Equal("Online", Field(parsed, "LogCategoryFilter")); // the player's own line survives
            Assert.Equal(lanMode ? "0.0.0.0" : null, Field(parsed, "LocalAnubisHost"));
            Assert.Null(Field(parsed, "Credential")); // no -Credential: the anonymous credential comes from native
        }

        private static object Field(object instance, string name) => instance.GetType().GetField(name).GetValue(instance);

        private Type LoadArgumentsType()
        {
            string dir = Environment.GetEnvironmentVariable("MCVS_REFS");
            if (string.IsNullOrEmpty(dir)) dir = "/home/user/mcvs-work/extracted/assemblies";
            string engine = Path.Combine(dir, "Gameloft.Engine.dll");
            if (!File.Exists(engine))
            {
                if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                    throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but " + engine + " is missing");
                output.WriteLine("Gameloft.Engine.dll not found in MCVS_REFS (" + dir + "): skipped");
                return null;
            }
            var context = new GameContext(dir);
            return context.LoadFromAssemblyPath(engine).GetType("Gameloft.Engine.GameApplication+Arguments", true);
        }

        /// <summary>The game's own assemblies from the folder; the BCL from net8.</summary>
        private sealed class GameContext : AssemblyLoadContext
        {
            private readonly string directory;

            public GameContext(string directory) : base("mcvs-args-conformance", isCollectible: true)
            {
                this.directory = directory;
            }

            protected override Assembly Load(AssemblyName name)
            {
                string simple = name.Name;
                if (simple == "mscorlib" || simple == "netstandard" || simple == "System" || simple.StartsWith("System.", StringComparison.Ordinal)
                    || simple.StartsWith("Microsoft.", StringComparison.Ordinal))
                    return null;
                string path = Path.Combine(directory, simple + ".dll");
                return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
            }
        }
    }
}
