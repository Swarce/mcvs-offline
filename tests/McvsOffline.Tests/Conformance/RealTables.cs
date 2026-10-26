using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using McvsOffline.Patcher.Patches;
using McvsOffline.Server.Core.Content;
using McvsOffline.Tests.Content;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit.Abstractions;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace McvsOffline.Tests.Conformance
{
    /// <summary>
    /// The real client the Cosmetic crate's conformance tests run against (docs/design/economy-collector.md §6 "WP-F"):
    /// <list type="bullet">
    /// <item>the player's assemblies (MCVS_REFS) in a load context of their own, with <c>Game.dll</c> carrying the
    /// patcher's default <c>store</c> patch, applied in memory by <see cref="StorePatch"/> itself (as RealStorePatchTests
    /// does): the collector requires it (D11, §2.13);</item>
    /// <item><c>Design/MCVS_Meta.xlsx</c> of the player's data pack (MCVS_DATAPACK through the Core <see cref="DataPack"/>,
    /// else the decoded copy in MCVS_DATAPACK_DECODED) as real <c>Gameloft.Engine.Database.Table</c>s, each made the way
    /// <c>Database.Load</c> makes it (EN/Database.cs: <c>Json.LoadObject</c>, then <c>JToken.Populate</c> into a new
    /// <c>Table</c>), fresh for every use because a CRM changer edits its rows in place (the bench technique of
    /// collector/rev/bench2, with the game's own loader instead of a hand-made copy).</item>
    /// </list>
    /// Nothing reaches native code: the <c>Database</c> the tables point to is uninitialised (its constructor reads
    /// <c>GameApplication.Instance</c>). Skipped without the assemblies or the data pack unless MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    internal sealed class RealTables
    {
        public const string MetaPath = "Design/MCVS_Meta.xlsx";

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly object Gate = new object();
        private static RealTables shared;

        private readonly PatchedGameContext context;
        private readonly Dictionary<string, object> meta = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly MethodInfo populate;
        private readonly object database;
        private readonly Func<string, string> readText;
        private readonly Func<string, bool> exists;

        private RealTables(string refs, Dictionary<string, byte[]> patched, string metaText, Func<string, string> readText,
                           Func<string, bool> exists, List<string> notes)
        {
            context = new PatchedGameContext(refs, patched);
            PatchNotes = notes;
            this.readText = readText;
            this.exists = exists;
            Engine = Assembly("Gameloft.Engine");
            Game = Assembly("Game");
            Util = Assembly("Gameloft.Util");
            TableType = Engine.GetType("Gameloft.Engine.Database+Table", true);
            Type databaseType = Engine.GetType("Gameloft.Engine.Database", true);
            database = RuntimeHelpers.GetUninitializedObject(databaseType);
            databaseType.GetProperty("Tables", Any).SetValue(database, Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), TableType)));

            // EN/Database.cs Load: Json.LoadObject(text), then item.Value.Populate(new Table { Database, Name }) per table.
            Type json = Util.GetType("Gameloft.Util.Json", true);
            object root = json.GetMethod("LoadObject", new[] { typeof(string) }).Invoke(null, new object[] { metaText });
            foreach (object property in (IEnumerable)GameClient.Call(root, "Properties"))
                meta[(string)GameClient.Get(property, "Name")] = GameClient.Get(property, "Value");
            populate = Util.GetType("Gameloft.Util.JTokenExtentions", true).GetMethods(BindingFlags.Public | BindingFlags.Static)
                           .Single(m => m.Name == "Populate" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)
                           .MakeGenericMethod(TableType);
            RegisterGameAssembly();
        }

        /// <summary>The shared instance, or null (after a note) when the assemblies or the data pack are missing and not required.</summary>
        public static RealTables TryGet(ITestOutputHelper output)
        {
            string refs = GameClient.RefsDirectory;
            foreach (string name in new[] { "Gameloft.Engine", "Game", "Gameloft.Util", "Gameloft.Templates", "Gameloft.OnlinePlatform" })
            {
                if (File.Exists(Path.Combine(refs, name + ".dll"))) continue;
                if (GameClient.Required) throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but " + Path.Combine(refs, name + ".dll") + " is missing");
                output?.WriteLine(name + ".dll not found in MCVS_REFS (" + refs + "): skipped");
                return null;
            }
            string pack = ContentTestSupport.RealDataPack;
            string decoded = ContentTestSupport.DecodedDataPack;
            if (pack == null && decoded == null)
            {
                if (GameClient.Required)
                    throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but no data pack for " + MetaPath + " (MCVS_DATAPACK or MCVS_DATAPACK_DECODED)");
                output?.WriteLine("no data pack (MCVS_DATAPACK / MCVS_DATAPACK_DECODED): skipped");
                return null;
            }
            lock (Gate)
            {
                if (shared != null) return shared;
                Func<string, string> readText;
                Func<string, bool> exists;
                if (pack != null)
                {
                    var dataPack = new DataPack(pack);
                    readText = path => dataPack.TryReadText(path, out string text) ? text : null;
                    exists = dataPack.Contains;
                    output?.WriteLine("data pack: " + pack);
                }
                else
                {
                    readText = path => File.Exists(Path.Combine(decoded, path)) ? File.ReadAllText(Path.Combine(decoded, path)) : null;
                    exists = path => File.Exists(Path.Combine(decoded, path));
                    output?.WriteLine("decoded data pack: " + decoded);
                }
                string metaText = readText(MetaPath) ?? throw new InvalidOperationException(MetaPath + " is not in the data pack");
                var notes = new List<string>();
                Dictionary<string, byte[]> patched = PatchStore(refs, notes);
                shared = new RealTables(refs, patched, metaText, readText, exists, notes);
                return shared;
            }
        }

        public Assembly Engine { get; }

        public Assembly Game { get; }

        public Assembly Util { get; }

        /// <summary><c>Gameloft.Engine.Database+Table</c>.</summary>
        public Type TableType { get; }

        /// <summary>What the <c>store</c> patch reported (one note per change).</summary>
        public List<string> PatchNotes { get; }

        public Assembly Assembly(string name) => context.LoadFromAssemblyName(new AssemblyName(name));

        /// <summary>A type of a game assembly, e.g. ("Game", "Game.CratesSpecsCollection").</summary>
        public Type Type(string assembly, string fullName) => Assembly(assembly).GetType(fullName, true);

        /// <summary>The MCVS_Meta table names.</summary>
        public ICollection<string> TableNames => meta.Keys;

        public bool HasTable(string name) => meta.ContainsKey(name);

        /// <summary>A new <c>Database.Table</c> holding the shipped rows of <paramref name="name"/>.</summary>
        public object NewTable(string name)
        {
            if (!meta.TryGetValue(name, out object token)) throw new KeyNotFoundException("MCVS_Meta has no table " + name);
            object table = Activator.CreateInstance(TableType);
            GameClient.Set(table, "Database", database);
            GameClient.Set(table, "Name", name);
            populate.Invoke(null, new[] { token, table });
            return table;
        }

        /// <summary>
        /// The CRM changer the client builds for MCVS_Meta from a <c>game</c> object (the TS's and the client's
        /// <c>RawGameParameters</c>) or a mirror (<c>_ServerConfig.default</c>, what server Worlds install):
        /// <c>CRMComponent.GetChanger(gameParams, "Design/MCVS_Meta.xlsx")</c> (EN/CRMComponent.cs).
        /// </summary>
        public object Changer(object gameParams) => Changer(gameParams, MetaPath);

        /// <summary>The changer for the database at <paramref name="databasePath"/> (e.g. "Design/MCVS_Gameplay.xlsx").</summary>
        public object Changer(object gameParams, string databasePath)
        {
            Type crm = Type("Gameloft.Engine", "Gameloft.Engine.CRMComponent");
            return crm.GetMethod("GetChanger", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { gameParams, databasePath });
        }

        /// <summary><c>Gameloft.Util.Json.LoadObject(text)</c>: a JSON object on the game's Newtonsoft (the GS reads the
        /// <c>game</c> selector's body this way).</summary>
        public object LoadObject(string json)
        {
            return Util.GetType("Gameloft.Util.Json", true).GetMethod("LoadObject", new[] { typeof(string) }).Invoke(null, new object[] { json });
        }

        /// <summary>The changer's table changers by table name (the changer holds one per table).</summary>
        public Dictionary<string, object> TableChangers(object changer)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (object tableChanger in (IEnumerable)GameClient.Get(changer, "TableChangers"))
                result[(string)GameClient.Get(tableChanger, "TableName")] = tableChanger;
            return result;
        }

        /// <summary><c>Table.ApplyCRMTableChanges(changer)</c> on a fresh copy of the changer's table; null when MCVS_Meta has
        /// no such table (the client skips such a changer, EN/Database.cs ApplyCRMTableChanges).</summary>
        public object Applied(object tableChanger)
        {
            string name = (string)GameClient.Get(tableChanger, "TableName");
            if (!HasTable(name)) return null;
            object table = NewTable(name);
            GameClient.Call(table, "ApplyCRMTableChanges", tableChanger);
            return table;
        }

        /// <summary>A typed table (<c>TableLoader.TableBase</c>) filled from <paramref name="table"/>, as <c>Database.GetTable</c> does.</summary>
        public object Fill(string assembly, string collectionType, object table)
        {
            object collection = Activator.CreateInstance(Type(assembly, collectionType));
            GameClient.Call(collection, "FillDataWithTable", table);
            return collection;
        }

        /// <summary>The row whose <paramref name="indexField"/> equals <paramref name="key"/> as the CRM finds it
        /// (<c>GetRowByFieldValue(field, string)</c>), or null.</summary>
        public static object[] Row(object table, string indexField, string key)
        {
            MethodInfo find = table.GetType().GetMethods().Single(m => m.Name == "GetRowByFieldValue" && m.IsGenericMethodDefinition
                                                                       && m.GetParameters()[0].ParameterType == typeof(string));
            return (object[])find.MakeGenericMethod(typeof(string)).Invoke(table, new object[] { indexField, key });
        }

        public static int FieldIndex(object table, string field) => (int)GameClient.Call(table, "GetFieldIndex", field);

        /// <summary>A text file of the data pack (null when absent).</summary>
        public string ReadText(string path) => readText(path);

        /// <summary>True when the data pack has <paramref name="path"/> (an image, a localization file).</summary>
        public bool Exists(string path) => exists(path);

        /// <summary>
        /// ComponentAssemblies.InitGameAssemblies for this context's engine: Game.dll is a "game assembly", so
        /// <c>TypeUtil.GetType</c> resolves the <c>&lt;Type&gt;RewardItem</c> classes behind reward strings.
        /// </summary>
        private void RegisterGameAssembly()
        {
            Type components = Engine.GetType("Gameloft.Engine.ComponentAssemblies", true);
            var list = (IList)components.GetField("gameAssemblies", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            if (!list.Contains(Game)) list.Add(Game);
        }

        // ------------------------------------------------------------------------------------------ the store patch

        /// <summary>
        /// Game.dll of MCVS_REFS with <see cref="StorePatch"/> applied, as the patcher's default run does (the hooks it
        /// imports come from a stand-in McvsOffline.Server module that has only <c>McvsOffline.GameHooks.StoreItemException</c>;
        /// the patched filter calls it only when a shop item's check throws, which nothing here does).
        /// </summary>
        private static Dictionary<string, byte[]> PatchStore(string refs, List<string> notes)
        {
            using (var resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(refs);
                foreach (string d in resolver.GetSearchDirectories().Where(d => d != refs).ToList()) resolver.RemoveSearchDirectory(d);
                var reader = new ReaderParameters { AssemblyResolver = resolver, InMemory = true, ReadingMode = ReadingMode.Immediate };
                using (ModuleDefinition server = HookModule())
                using (ModuleDefinition game = ModuleDefinition.ReadModule(Path.Combine(refs, "Game.dll"), reader))
                {
                    var patchContext = new PatchContext(server, 51780);
                    new StorePatch().Apply(game, patchContext);
                    notes.AddRange(patchContext.Notes);
                    var stream = new MemoryStream();
                    game.Write(stream);
                    return new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["Game"] = stream.ToArray() };
                }
            }
        }

        private static ModuleDefinition HookModule()
        {
            AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("McvsOffline.Server", new Version(0, 0, 0, 0)),
                                                                            "McvsOffline.Server.dll", ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            var hooks = new TypeDefinition(NativeOnlinePatch.HooksType.Substring(0, NativeOnlinePatch.HooksType.LastIndexOf('.')),
                                           NativeOnlinePatch.HooksType.Substring(NativeOnlinePatch.HooksType.LastIndexOf('.') + 1),
                                           TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class,
                                           module.TypeSystem.Object);
            var report = new MethodDefinition("StoreItemException", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                                              module.TypeSystem.Void);
            report.Parameters.Add(new ParameterDefinition("item", ParameterAttributes.None, module.TypeSystem.Object));
            report.Parameters.Add(new ParameterDefinition("exception", ParameterAttributes.None,
                                                          new TypeReference("System", "Exception", module, module.TypeSystem.CoreLibrary)));
            report.Body.GetILProcessor().Emit(OpCodes.Ret);
            hooks.Methods.Add(report);
            module.Types.Add(hooks);
            return module;
        }

        /// <summary>The game's assemblies from the folder, the patched ones from memory; the BCL always comes from net8.</summary>
        private sealed class PatchedGameContext : AssemblyLoadContext
        {
            private readonly string directory;
            private readonly Dictionary<string, byte[]> patched;

            public PatchedGameContext(string directory, Dictionary<string, byte[]> patched) : base("mcvs-collector-conformance")
            {
                this.directory = directory;
                this.patched = patched;
            }

            protected override Assembly Load(AssemblyName name)
            {
                string simple = name.Name;
                if (simple == "mscorlib" || simple == "netstandard" || simple == "System" || simple.StartsWith("System.", StringComparison.Ordinal)
                    || simple.StartsWith("Microsoft.", StringComparison.Ordinal))
                    return null;
                if (patched.TryGetValue(simple, out byte[] bytes)) return LoadFromStream(new MemoryStream(bytes));
                string path = Path.Combine(directory, simple + ".dll");
                return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
            }
        }
    }
}
