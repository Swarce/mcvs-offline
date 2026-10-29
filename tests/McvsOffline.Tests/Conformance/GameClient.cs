using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Conformance
{
    /// <summary>
    /// The player's own client assemblies (Gameloft.OnlinePlatform, Gameloft.Engine, Game, Gameloft.Templates,
    /// Gameloft.Util and their Newtonsoft.Json 7), loaded from MCVS_REFS into an isolated load context on net8.
    /// They are never committed; tests skip when the folder lacks them, unless MCVS_REQUIRE_REAL_CLIENT=1
    /// (scripts/build.sh sets it when the folder has them), in which case a missing DLL fails the test.
    /// <para>
    /// The game's assemblies reference the Xamarin mscorlib 2.0.5; with the BCL left to net8 (the load context only
    /// supplies the game's own assemblies) they bind to the .NET facades. Code that would reach native code (the
    /// engine's GameObjects, JNI) is never called: where the game builds an object through native state, the harness
    /// supplies the equivalent (documented at each use).
    /// </para>
    /// <para>
    /// One client per folder (<see cref="For"/>): the Android APK's (MCVS_REFS) and the Steam install's (MCVS_PC_REFS,
    /// docs/design/pc-port.md §4.7), whose assemblies reference mscorlib 4.0.0.0 and bind to the same facades. The PC
    /// Newtonsoft.Json 7.0.1 is the net45 build and needs System.Security.Permissions, which this project references.
    /// </para>
    /// Helpers return <c>dynamic</c> so calls bind on the runtime types.
    /// </summary>
    public sealed class GameClient
    {
        public const string DefaultRefs = TestFolders.DefaultAndroidRefs;

        /// <summary>One client per folder (one load context each): the Android APK's and the Steam install's (§4.7).</summary>
        private static readonly ConcurrentDictionary<string, Lazy<GameClient>> Clients =
            new ConcurrentDictionary<string, Lazy<GameClient>>(StringComparer.Ordinal);

        private readonly GameLoadContext context;

        private GameClient(string directory)
        {
            Directory = directory;
            context = new GameLoadContext(directory);
            OnlinePlatform = context.LoadFromAssemblyName(new AssemblyName("Gameloft.OnlinePlatform"));
        }

        /// <summary>The folder this client's assemblies come from.</summary>
        public string Directory { get; }

        /// <summary>The Android folder (MCVS_REFS).</summary>
        public static string RefsDirectory => TestFolders.AndroidRefs;

        /// <summary>MCVS_REQUIRE_REAL_CLIENT=1: the Android client's files are mandatory.</summary>
        public static bool Required => TestFolders.Required(TestFolders.Android);

        /// <summary>The client loaded from <paramref name="folder"/> (created once per folder).</summary>
        public static GameClient For(string folder)
        {
            string key = Path.GetFullPath(folder);
            return Clients.GetOrAdd(key, k => new Lazy<GameClient>(() => new GameClient(k))).Value;
        }

        /// <summary>The Android client (MCVS_REFS), or null (after a note) when the assemblies are absent and not required.</summary>
        public static GameClient TryGet(ITestOutputHelper output, params string[] assemblies)
        {
            return TryGetFrom(output, TestFolders.Android, RefsDirectory, assemblies);
        }

        /// <summary>
        /// The client of <paramref name="platform"/> in <paramref name="folder"/> (a row of
        /// <see cref="TestFolders.RealClientFolders"/>), or null (after a note) when an assembly is absent and the
        /// platform's files are not required (MCVS_REQUIRE_REAL_CLIENT, MCVS_REQUIRE_PC).
        /// </summary>
        public static GameClient TryGetFrom(ITestOutputHelper output, string platform, string folder, params string[] assemblies)
        {
            var needed = new List<string> { "Gameloft.OnlinePlatform" };
            needed.AddRange(assemblies);
            foreach (string name in needed)
            {
                string path = Path.Combine(folder, name + ".dll");
                if (File.Exists(path)) continue;
                if (TestFolders.Required(platform))
                    throw new InvalidOperationException("the " + platform + " client is required but " + path + " is missing");
                output?.WriteLine(name + ".dll not found in " + TestFolders.VariableOf(platform) + " (" + folder + "): skipped");
                return null;
            }
            return For(folder);
        }

        public Assembly OnlinePlatform { get; }

        /// <summary>
        /// What the engine does at start-up from its GameAssemblies config (ComponentAssemblies.InitGameAssemblies):
        /// Game.dll becomes a "game assembly", so TypeUtil.GetType resolves "Game.*" types such as the
        /// "&lt;Type&gt;RewardItem" classes behind reward strings.
        /// </summary>
        public void RegisterGameAssemblies()
        {
            lock (context)
            {
                if (gameAssembliesRegistered) return;
                Type components = Type("Gameloft.Engine", "Gameloft.Engine.ComponentAssemblies");
                var list = (IList)components.GetField("gameAssemblies", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                list.Add(Assembly("Game"));
                gameAssembliesRegistered = true;
            }
        }

        private bool gameAssembliesRegistered;

        public Assembly Assembly(string name) => context.LoadFromAssemblyName(new AssemblyName(name));

        /// <summary>A Gameloft.OnlinePlatform type by simple name ("Federation", "Federation+Protocol").</summary>
        public Type Op(string name) => OnlinePlatform.GetType("Gameloft.OnlinePlatform." + name, true);

        /// <summary>A type from another game assembly, e.g. ("Gameloft.Engine", "Gameloft.Engine.LocalAnubis.GameConfig").</summary>
        public Type Type(string assembly, string fullName) => Assembly(assembly).GetType(fullName, true);

        public dynamic New(Type type, params object[] args) => Activator.CreateInstance(type, args);

        public dynamic Credential(string text) => New(Op("Credential"), text);

        public dynamic Enum(Type type, string value) => System.Enum.Parse(type, value);

        public static dynamic Static(Type type, string member)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo field = type.GetField(member, flags);
            if (field != null) return field.GetValue(null);
            return type.GetProperty(member, flags).GetValue(null);
        }

        /// <summary>A List&lt;T&gt; of a game type.</summary>
        public dynamic List(Type elementType, IEnumerable items = null)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType));
            if (items != null) foreach (object item in items) list.Add(item);
            return list;
        }

        /// <summary>session.GetService&lt;T&gt;() for an OnlinePlatform service type name.</summary>
        public dynamic Service(object session, string serviceType) =>
            CallGeneric(session, "GetService", new[] { Op(serviceType) });

        /// <summary>Calls a (possibly non-public) instance method by name and argument count.</summary>
        public static dynamic Call(object target, string method, params object[] args) =>
            Find(target.GetType(), method, args.Length, false).Invoke(target, args);

        /// <summary>Calls a (possibly non-public) static method by name and argument count.</summary>
        public static dynamic CallStatic(Type type, string method, params object[] args) =>
            Find(type, method, args.Length, true).Invoke(null, args);

        /// <summary>Calls a generic instance method, e.g. profile.GetProfileSection&lt;T&gt;(name).</summary>
        public static dynamic CallGeneric(object target, string method, Type[] typeArgs, params object[] args)
        {
            MethodInfo m = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                 .First(x => x.Name == method && x.IsGenericMethodDefinition && x.GetParameters().Length == args.Length
                                             && x.GetGenericArguments().Length == typeArgs.Length);
            return m.MakeGenericMethod(typeArgs).Invoke(target, args);
        }

        public static dynamic Get(object target, string member)
        {
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                PropertyInfo p = t.GetProperty(member, flags);
                if (p != null) return p.GetValue(target);
                FieldInfo f = t.GetField(member, flags);
                if (f != null) return f.GetValue(target);
            }
            throw new MissingMemberException(target.GetType().FullName, member);
        }

        public static void Set(object target, string member, object value)
        {
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                PropertyInfo p = t.GetProperty(member, flags);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(target, value);
                    return;
                }
                FieldInfo f = t.GetField(member, flags) ?? t.GetField("<" + member + ">k__BackingField", flags);
                if (f != null)
                {
                    f.SetValue(target, value);
                    return;
                }
            }
            throw new MissingMemberException(target.GetType().FullName, member);
        }

        /// <summary>
        /// An instance of a game type whose own constructor cannot run here (it reads native engine state), with the
        /// constructor of <paramref name="baseType"/> run on it instead, so every field initializer and constructor
        /// statement of the base class is the game's own code. The caller replays the rest of the skipped constructor.
        /// </summary>
        public static object Construct(Type type, Type baseType, params object[] baseCtorArgs)
        {
            object instance = RuntimeHelpers.GetUninitializedObject(type);
            ConstructorInfo ctor = baseType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                           .Single(c => c.GetParameters().Length == baseCtorArgs.Length);
            ctor.Invoke(instance, baseCtorArgs);
            return instance;
        }

        /// <summary>Awaits a Task or Task&lt;T&gt; returned through dynamic/reflection and gives its result (null for a plain Task).</summary>
        public static async Task<dynamic> Await(object task, int timeoutMs = 30000)
        {
            var t = (Task)task;
            if (await Task.WhenAny(t, Task.Delay(timeoutMs)) != t) throw new TimeoutException("the client call did not finish in " + timeoutMs + " ms");
            await t;
            PropertyInfo result = t.GetType().GetProperty("Result");
            return result == null || result.PropertyType.Name == "VoidTaskResult" ? null : result.GetValue(t);
        }

        /// <summary>The HTTP status carried by a client exception (OnlineRequestFailedException.Response), or 0.</summary>
        public static int StatusOf(Exception failure)
        {
            for (Exception e = failure; e != null; e = e.InnerException)
            {
                PropertyInfo responseProperty = e.GetType().GetProperty("Response");
                if (responseProperty?.GetValue(e) is System.Net.Http.HttpResponseMessage response) return (int)response.StatusCode;
            }
            return 0;
        }

        private static MethodBase Find(Type type, string method, int argCount, bool isStatic)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            for (Type t = type; t != null; t = t.BaseType)
            {
                MethodInfo m = t.GetMethods(flags | BindingFlags.DeclaredOnly)
                                .FirstOrDefault(x => x.Name == method && !x.IsGenericMethodDefinition && x.GetParameters().Length == argCount);
                if (m != null) return m;
            }
            throw new MissingMethodException(type.FullName, method + "/" + argCount);
        }

        /// <summary>Loads Gameloft.* and their Newtonsoft copy from the folder; the BCL always comes from net8.</summary>
        private sealed class GameLoadContext : AssemblyLoadContext
        {
            private readonly string directory;

            public GameLoadContext(string directory) : base("mcvs-game-client-conformance")
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
