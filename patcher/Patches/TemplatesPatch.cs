using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Default 'templates' patch of Gameloft.Templates.dll (build 32; the owner's build-31 log: agent selection froze for 27.7 s
    /// while the GS spawned its 7 bots, about 4 s each, and the 3.7-4.3 s profile reloads and the first profile load of each World
    /// stood in the same code).
    /// <para>
    /// Every bot gets the human's profile as its template: <c>MultiplayerComponent.CreateBotWithProfile</c> →
    /// <c>GetProfileTemplate</c> → <c>GameProfileSection.GetTemplate()</c>, which reads the ~611 KB unlock-all ProfileString again with
    /// <c>JsonTemplateReader</c>. The reader binds each property of a JSON object with <c>BindNodes</c>: <c>parent.HasName(name)</c>,
    /// then <c>parent.Add(child)</c>, which scans the children again for a conflict, so an object of k properties costs about k²
    /// name comparisons. <c>CrmStandardData.Inventory</c> has one property per unlock-all ledger key (11,368): about 129 million
    /// comparisons per read (the thread dump at the freeze stood in <c>BindNodes → HasName → TNode.Node → TName.op_Equality</c>).
    /// </para>
    /// <para>
    /// The change, in <c>JsonTemplateReader</c> only (the same IL on both builds):
    /// <code>
    /// private TObject ReadObject(TName name, JObject jo)
    /// {
    ///     Dictionary&lt;TName, TNode&gt; index = McvsIndexFor(jo);        // added: null below 8 properties
    ///     ...the game's loop, with McvsBind(tObject, child, index) instead of BindNodes(tObject, child)...
    /// }
    /// private void McvsBind(TNode parent, TNode child, Dictionary&lt;TName, TNode&gt; index)
    /// {
    ///     TName n;
    ///     if (index == null || (n = child.Name).Name == null || n.Type == TNameType.Attribute) { BindNodes(parent, child); return; }
    ///     TNode found;
    ///     if (index.TryGetValue(n, out found)) { found.MergeWith(child); return; }   // BindNodes: Node(name).MergeWith(child)
    ///     parent.Add(child, false);                                                    // BindNodes: Add(child) after HasName was false
    ///     index.Add(n, child);
    /// }
    /// </code>
    /// The parent is the reader's own new <c>TObject</c>; during the loop its child list changes only through this method (a
    /// merge changes the found child's subtree, never the parent's list, and keeps the child's name, which equals the merged
    /// one), so the index always holds exactly the node <c>Node(name)</c> would find first, and the conflict check after a
    /// negative <c>HasName</c> can never fire (<c>Add</c> still throws "Empty name" as before). Attribute names (<c>@base</c>
    /// among them) and a null name take the game's <c>BindNodes</c>. <c>ReadArray</c> adds each element with
    /// <c>Add(node, false)</c> instead of <c>Add(node, true)</c>: <c>TArray.Add</c> names a new element <c>[Count]</c>, which no
    /// earlier element of the new array has. The tree is the shipped one node for node (names, types, values, order, parents,
    /// annotations, bases); every bot still gets a tree of its own.
    /// </para>
    /// <para>
    /// For the next device log, <c>ReadDefaultTemplate</c>, <c>LoadRoot</c> and <c>TObject.FromObject</c> also report their times to
    /// <c>McvsOffline.GameDataHooks</c> (a template read of 100 ms or more, or with an object of 1,000 names or more, gets a
    /// "templates:" line, as does a <c>FromObject</c> of 100 ms or more: the profile save's writer). Without McvsOffline.Server.dll
    /// those calls return at once (a <c>hooksMissing</c> latch); the reader change needs no server.
    /// </para>
    /// <para>
    /// Second part (build 32, the profile saves and the profile loads' merge): the template writer
    /// (<c>DefaultConverter.ConvertToTemplate</c>, behind <c>TObject.FromObject</c> of every save), <c>TNode.Copy</c> and
    /// <c>TNode.MergeWith</c> (<c>ValidateProfileVersion</c>: the default profile merged with the loaded one) added each child with
    /// the conflict scan, about 65 million name comparisons for the Inventory per save and per merge. They now keep a set of a
    /// <c>TObject</c>'s names from its 8th child; see <c>ApplyTrees</c> for the argument, and <c>TObject.ToString("j")</c> reports
    /// its time too.
    /// </para>
    /// Every changed method, and every game method the equivalence relies on, is checked against the IL fingerprint of the
    /// verified build first.
    /// </summary>
    public sealed class TemplatesPatch : IlPatch
    {
        public const string ReaderType = "Gameloft.Templates.JsonTemplateReader";
        public const string NodeType = "Gameloft.Templates.TNode";
        public const string ObjectType = "Gameloft.Templates.TObject";
        public const string ArrayType = "Gameloft.Templates.TArray";
        public const string ValueType = "Gameloft.Templates.TValue";
        public const string NameType = "Gameloft.Templates.TName";
        public const string HelperNamespace = "Gameloft.Templates";
        public const string HelperType = "McvsTemplates";
        public const string HooksType = "McvsOffline.GameDataHooks";

        /// <summary>An object with fewer properties keeps the game's linear lookups (cheap, and no dictionary to allocate).</summary>
        public const int IndexMinProperties = 8;

        /// <summary>TNameType.Attribute.</summary>
        private const int AttributeType = 8;

        public const string ReadObjectFingerprint = "77e0edc3ca385992ab7a3bf80d313c6b1c1cf8a20e62555d1eb32a592ef8823d";
        public const string PcReadObjectFingerprint = ReadObjectFingerprint;
        public const string ReadArrayFingerprint = "758c876b7ea11f51385a83c49d1e916a70baee314bca2ecd7688304546cafb1f";
        public const string PcReadArrayFingerprint = ReadArrayFingerprint;
        public const string ReadDefaultTemplateFingerprint = "8222b9f5318ff75e6ce6727e2381374256201e20e7bae2125692afefcdf200e9";
        public const string PcReadDefaultTemplateFingerprint = ReadDefaultTemplateFingerprint;
        public const string LoadRootFingerprint = "22ccaae808203c1d3a6767245e1459bad6ab46a891dc0e59d6794ee5c2ccd1e7";
        public const string PcLoadRootFingerprint = LoadRootFingerprint;
        public const string FromObjectFingerprint = "dae7e31569f808f61fda749de84651683971839aa05667bcd2da56144d43e0ca";
        public const string PcFromObjectFingerprint = FromObjectFingerprint;

        /// <summary>
        /// <see cref="Combined"/> of <see cref="ReliedOn"/>: the game methods the equivalence argument above reads (BindNodes, the
        /// two ReadNode, ReadValue; TNode's Add, Node, HasName, MergeWith, name accessors and ChangeName; TArray's and TValue's Add
        /// and MergeWith; TName's equality, hash and accessors).
        /// </summary>
        public const string ReliedOnFingerprint = "a6f48ebfec5fe8cf19d854c92855806edd933f1143af3ebb7903d0db2407427a";
        public const string PcReliedOnFingerprint = ReliedOnFingerprint;

        public const string ConvertToTemplateFingerprint = "cc46bcf4a998c1e713c133cdad7da5f168c910c894422aeaf52dc55335e28375";
        public const string PcConvertToTemplateFingerprint = ConvertToTemplateFingerprint;
        public const string CopyFingerprint = "92ddbc35f1a5a692037ab15306d76e0c751d6f1ae5c6de7ab638daeb31ab1159";
        public const string PcCopyFingerprint = CopyFingerprint;
        public const string ToStringFingerprint = "e4de93983db2fd44d1be41d8d2ffe48a0d91bbb1152adb44f24d5e1a02bc9515";
        public const string PcToStringFingerprint = ToStringFingerprint;

        /// <summary>
        /// <see cref="Combined"/> of <see cref="TreesReliedOn"/>: the game methods the argument for the writer, Copy and MergeWith
        /// changes reads beyond <see cref="ReliedOn"/> (TNode's CopyNode, SetNode, RemoveNode, Count; TemplateExtensions.GetValue;
        /// TValue.Value; TemplateActivator.CreateTemplate; the CreateTemplateNode of DefaultConverter, DictionaryConverter and
        /// ListConverter; ValueConverter.ConvertToTemplate).
        /// </summary>
        public const string TreesReliedOnFingerprint = "bca00f424a30b616f363c6bf9959f63ce68ef9cd7b41be977ad1d06b65fec6e8";
        public const string PcTreesReliedOnFingerprint = TreesReliedOnFingerprint;

        public const string ConverterType = "Gameloft.Templates.DefaultConverter";
        public const string ExtensionsType = "Gameloft.Templates.TemplateExtensions";

        public override string Id => "templates";

        public override string AssemblyFile => "Gameloft.Templates.dll";

        public override string Description => "templates: the template reader finds a JSON object's properties with a dictionary instead of scanning them twice per property (JsonTemplateReader.ReadObject; bots at agent select, profile loads), and the writer, Copy and MergeWith find a TObject's names with a set instead of a scan per child (DefaultConverter.ConvertToTemplate, TNode.Copy/MergeWith; profile saves and loads; Gameloft.Templates.dll)";

        /// <summary>The methods behind <see cref="ReliedOnFingerprint"/>, in a fixed order; a missing one is a <see cref="PatchException"/>.</summary>
        public static List<MethodDefinition> ReliedOn(ModuleDefinition module)
        {
            TypeDefinition reader = RequireType(module, ReaderType), node = RequireType(module, NodeType), array = RequireType(module, ArrayType),
                           value = RequireType(module, ValueType), name = RequireType(module, NameType);
            return new List<MethodDefinition>
            {
                One(reader, "BindNodes", 2, NodeType),
                One(reader, "ReadNode", 1, "Newtonsoft.Json.Linq.JProperty"),
                One(reader, "ReadNode", 2, NameType),
                One(reader, "ReadValue", 2, NameType),
                One(node, "Add", 2, NodeType),
                One(node, "Node", 1, NameType),
                One(node, "HasName", 1, NameType),
                One(node, "MergeWith", 1, NodeType),
                One(node, "get_Name", 0, null),
                One(node, "set_Name", 1, NameType),
                One(node, "ChangeName", 2, NameType),
                One(node, "SetParentInternal", 1, NodeType),
                One(array, "Add", 2, NodeType),
                One(array, "MergeWith", 1, NodeType),
                One(value, "MergeWith", 1, NodeType),
                One(name, "op_Equality", 2, NameType),
                One(name, "Equals", 1, NameType),
                One(name, "GetHashCode", 0, null),
                One(name, "get_Name", 0, null),
                One(name, "get_Type", 0, null),
                One(name, "get_IsEmpty", 0, null),
            };
        }

        /// <summary>The methods behind <see cref="TreesReliedOnFingerprint"/>, in a fixed order.</summary>
        public static List<MethodDefinition> TreesReliedOn(ModuleDefinition module)
        {
            TypeDefinition node = RequireType(module, NodeType), value = RequireType(module, ValueType), ext = RequireType(module, ExtensionsType),
                           activator = RequireType(module, "Gameloft.Templates.TemplateActivator"), converter = RequireType(module, ConverterType),
                           dictionary = RequireType(module, "Gameloft.Templates.DictionaryConverter"), list = RequireType(module, "Gameloft.Templates.ListConverter"),
                           values = RequireType(module, "Gameloft.Templates.ValueConverter");
            return new List<MethodDefinition>
            {
                One(node, "CopyNode", 0, null),
                One(node, "SetNode", 2, NameType),
                One(node, "RemoveNode", 1, NodeType),
                One(node, "get_Count", 0, null),
                One(ext, "GetValue", 2, NodeType),
                One(value, "get_Value", 0, null),
                One(activator, "CreateTemplate", 2, "System.Object"),
                One(converter, "CreateTemplateNode", 2, "System.Object"),
                One(dictionary, "CreateTemplateNode", 2, "System.Object"),
                One(list, "CreateTemplateNode", 2, "System.Object"),
                One(values, "ConvertToTemplate", 2, "System.Object"),
            };
        }

        private static MethodDefinition One(TypeDefinition type, string name, int parameters, string firstParameter)
        {
            var matches = type.Methods.Where(m => m.Name == name && m.HasBody && m.Parameters.Count == parameters &&
                                                  (firstParameter == null || m.Parameters[0].ParameterType.FullName == firstParameter)).ToList();
            if (matches.Count != 1)
                throw new PatchException(type.FullName + ": expected exactly one " + name + "(" + parameters + " parameters) with a body, found " + matches.Count);
            return matches[0];
        }

        /// <summary>SHA-256 over "FullName fingerprint" lines of <paramref name="methods"/>.</summary>
        public static string Combined(IEnumerable<MethodDefinition> methods)
        {
            string text = string.Join("\n", methods.Select(m => m.FullName + " " + Fingerprint(m)));
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
                return string.Concat(hash.Select(b => b.ToString("x2")));
            }
        }

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify
            PatchSites.RequireBuildAssembly(module, context, Id);
            TypeDefinition reader = RequireType(module, ReaderType);
            TypeDefinition node = RequireType(module, NodeType);
            TypeDefinition tobject = RequireType(module, ObjectType);
            TypeDefinition name = RequireType(module, NameType);
            if (module.GetType(HelperNamespace + "." + HelperType) != null)
                throw new PatchException("Gameloft.Templates.dll is already patched: run the patcher on your original game files");
            if (!name.IsValueType) throw new PatchException(NameType + " is not a value type");

            MethodDefinition readObject = RequireMethod(reader, "ReadObject",
                m => !m.IsStatic && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == NameType &&
                     m.Parameters[1].ParameterType.FullName == "Newtonsoft.Json.Linq.JObject" && m.ReturnType.FullName == ObjectType,
                "instance TObject ReadObject(TName, JObject)");
            RequireFingerprint(readObject, ForBuild(context, ReadObjectFingerprint, PcReadObjectFingerprint), "JsonTemplateReader.ReadObject");
            MethodDefinition readArray = RequireMethod(reader, "ReadArray",
                m => !m.IsStatic && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == NameType &&
                     m.Parameters[1].ParameterType.FullName == "Newtonsoft.Json.Linq.JArray" && m.ReturnType.FullName == ArrayType,
                "instance TArray ReadArray(TName, JArray)");
            RequireFingerprint(readArray, ForBuild(context, ReadArrayFingerprint, PcReadArrayFingerprint), "JsonTemplateReader.ReadArray");
            MethodDefinition readDefault = RequireMethod(reader, "ReadDefaultTemplate",
                m => !m.IsStatic && m.Parameters.Count == 0 && m.ReturnType.FullName == ObjectType, "instance TObject ReadDefaultTemplate()");
            RequireFingerprint(readDefault, ForBuild(context, ReadDefaultTemplateFingerprint, PcReadDefaultTemplateFingerprint), "JsonTemplateReader.ReadDefaultTemplate");
            MethodDefinition loadRoot = RequireMethod(reader, "LoadRoot",
                m => !m.IsStatic && m.Parameters.Count == 0 && m.ReturnType.FullName == "Newtonsoft.Json.Linq.JObject", "instance JObject LoadRoot()");
            RequireFingerprint(loadRoot, ForBuild(context, LoadRootFingerprint, PcLoadRootFingerprint), "JsonTemplateReader.LoadRoot");
            MethodDefinition fromObject = RequireMethod(tobject, "FromObject",
                m => m.IsStatic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.Object &&
                     m.ReturnType.FullName == ObjectType, "static TObject FromObject(object)");
            RequireFingerprint(fromObject, ForBuild(context, FromObjectFingerprint, PcFromObjectFingerprint), "TObject.FromObject");
            List<MethodDefinition> reliedOn = ReliedOn(module);
            string combined = Combined(reliedOn);
            if (combined != ForBuild(context, ReliedOnFingerprint, PcReliedOnFingerprint))
            {
                throw new PatchException("Gameloft.Templates.dll: the methods the reader change relies on are not the verified code (combined fingerprint " +
                                         combined + "):\n" + string.Join("\n", reliedOn.Select(m => "    " + m.FullName + " " + Fingerprint(m))));
            }
            MethodDefinition bindNodes = reliedOn[0];
            MethodDefinition nodeAdd = reliedOn[4], nodeMergeWith = reliedOn[7], nodeGetName = reliedOn[8];
            MethodDefinition nameGetName = reliedOn[18], nameGetType = reliedOn[19];
            if (!nodeAdd.IsVirtual || !nodeMergeWith.IsVirtual) throw new PatchException(NodeType + ": Add/MergeWith are not virtual");

            // ReadObject: exactly one "ldarg.0; ldloc.0; ldloc.3; call BindNodes", not a branch target.
            var objectCode = readObject.Body.Instructions;
            List<Instruction> binds = objectCode.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m && m.Resolve() == bindNodes).ToList();
            if (binds.Count != 1 || binds[0].Previous.OpCode != OpCodes.Ldloc_3 || binds[0].Previous.Previous.OpCode != OpCodes.Ldloc_0 ||
                PatchSites.BranchesTo(readObject.Body, binds[0]).Count != 0 || readObject.Body.Variables.Count != 4 ||
                readObject.Body.Variables[0].VariableType.FullName != ObjectType)
                throw new PatchException(readObject.FullName + ": the BindNodes call is not where the fingerprint says:\n" + Dump(readObject));
            // ReadArray: exactly one "ldc.i4.1; callvirt TNode::Add(TNode, bool)".
            List<Instruction> adds = readArray.Body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference m && m.Resolve() == nodeAdd).ToList();
            if (adds.Count != 1 || adds[0].Previous.OpCode != OpCodes.Ldc_I4_1)
                throw new PatchException(readArray.FullName + ": the Add(node, true) call is not where the fingerprint says:\n" + Dump(readArray));
            // ReadDefaultTemplate: "ldarg.0; ldfld loaded; brtrue.s L; ldarg.0; call Load; L: ldarg.0; ldfld defaultTemplate; ret".
            var defaultCode = readDefault.Body.Instructions;
            if (defaultCode.Count != 8 || defaultCode[2].OpCode != OpCodes.Brtrue_S || defaultCode[2].Operand != defaultCode[5] ||
                !(defaultCode[4].Operand is MethodReference load && load.Name == "Load") || readDefault.Body.Variables.Count != 0)
                throw new PatchException(readDefault.FullName + ": not the verified body:\n" + Dump(readDefault));
            List<Instruction> rootReturns = loadRoot.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
            if (rootReturns.Count != 2 || rootReturns.Any(r => PatchSites.BranchesTo(loadRoot.Body, r).Count != 0))
                throw new PatchException(loadRoot.FullName + ": not the verified body:\n" + Dump(loadRoot));
            var fromCode = fromObject.Body.Instructions;
            if (fromCode.Count != 5 || fromCode[4].OpCode != OpCodes.Ret || fromObject.Body.Variables.Count != 0)
                throw new PatchException(fromObject.FullName + ": not the verified body:\n" + Dump(fromObject));
            FieldDefinition loaded = RequireField(reader, "loaded", "System.Boolean");
            FieldDefinition sourceName = RequireField(reader, "sourceName", "System.String");
            foreach (string field in new[] { "mcvsRooted", "mcvsIndexed", "mcvsLargest" })
                if (reader.Fields.Any(f => f.Name == field)) throw new PatchException(ReaderType + " already has a field " + field);
            TreeSites trees = VerifyTrees(module, context, node, tobject, nodeAdd, nodeMergeWith, nodeGetName, nameGetName);

            // ---- references
            TypeSystem ts = module.TypeSystem;
            var open = new TypeReference("System.Collections.Generic", "Dictionary`2", module, ts.CoreLibrary);
            open.GenericParameters.Add(new GenericParameter("TKey", open));
            open.GenericParameters.Add(new GenericParameter("TValue", open));
            if (open.Resolve() == null) throw new PatchException("the game's mscorlib has no System.Collections.Generic.Dictionary`2");
            var index = new GenericInstanceType(open);
            index.GenericArguments.Add(name);
            index.GenericArguments.Add(node);
            var newIndex = new MethodReference(".ctor", ts.Void, index) { HasThis = true };
            var tryGetValue = new MethodReference("TryGetValue", ts.Boolean, index) { HasThis = true };
            tryGetValue.Parameters.Add(new ParameterDefinition(open.GenericParameters[0]));
            tryGetValue.Parameters.Add(new ParameterDefinition(new ByReferenceType(open.GenericParameters[1])));
            var indexAdd = new MethodReference("Add", ts.Void, index) { HasThis = true };
            indexAdd.Parameters.Add(new ParameterDefinition(open.GenericParameters[0]));
            indexAdd.Parameters.Add(new ParameterDefinition(open.GenericParameters[1]));
            foreach (MethodReference m in new[] { newIndex, tryGetValue, indexAdd })
                if (m.Resolve() == null) throw new PatchException("the game's mscorlib has no " + m.FullName);
            TypeReference jobject = readObject.Parameters[1].ParameterType;
            var jcontainer = new TypeReference("Newtonsoft.Json.Linq", "JContainer", module, jobject.Scope);
            var getCount = new MethodReference("get_Count", ts.Int32, jcontainer) { HasThis = true };
            MethodDefinition count;
            try
            {
                count = getCount.Resolve();
            }
            catch (AssemblyResolutionException ex)
            {
                throw new PatchException("cannot resolve the game's Newtonsoft.Json for JContainer::get_Count: " + ex.Message);
            }
            if (count == null || count.IsStatic || !count.IsPublic) throw new PatchException("the game's Newtonsoft.Json has no public int JContainer::Count");
            MethodReference hookClock = ImportServerMethod(module, context, HooksType, "TemplateClock", "System.Int64");
            MethodReference hookRead = ImportServerMethod(module, context, HooksType, "TemplateRead", "System.Void",
                                                          "System.String", "System.Int64", "System.Int64", "System.Int32", "System.Int32");
            MethodReference hookFromObject = ImportServerMethod(module, context, HooksType, "TemplateFromObject", "System.Void", "System.Object", "System.Int64");
            MethodReference hookJson = ImportServerMethod(module, context, HooksType, "TemplateToJson", "System.Void", "System.String", "System.Int64");

            // ---- the helper type (timing only)
            TypeDefinition helper = PatchSites.AddHelperType(module, HelperNamespace, HelperType);
            var hooksMissing = new FieldDefinition("hooksMissing", FieldAttributes.Private | FieldAttributes.Static, ts.Boolean);
            helper.Fields.Add(hooksMissing);
            MethodDefinition clock = Latched(helper, hooksMissing, "Clock", ts.Int64, Forwarder(helper, "ForwardClock", ts.Int64, hookClock));
            MethodDefinition read = Latched(helper, hooksMissing, "Read", ts.Void,
                Forwarder(helper, "ForwardRead", ts.Void, hookRead, ts.String, ts.Int64, ts.Int64, ts.Int32, ts.Int32),
                ts.String, ts.Int64, ts.Int64, ts.Int32, ts.Int32);
            MethodDefinition converted = Latched(helper, hooksMissing, "FromObjectDone", ts.Void,
                Forwarder(helper, "ForwardFromObject", ts.Void, hookFromObject, ts.Object, ts.Int64), ts.Object, ts.Int64);
            MethodDefinition jsonDone = Latched(helper, hooksMissing, "JsonDone", ts.Void,
                Forwarder(helper, "ForwardJson", ts.Void, hookJson, ts.String, ts.Int64), ts.String, ts.Int64);

            // ---- the reader's new members
            var rooted = new FieldDefinition("mcvsRooted", FieldAttributes.Private, ts.Int64);
            var indexed = new FieldDefinition("mcvsIndexed", FieldAttributes.Private, ts.Int32);
            var largest = new FieldDefinition("mcvsLargest", FieldAttributes.Private, ts.Int32);
            reader.Fields.Add(rooted);
            reader.Fields.Add(indexed);
            reader.Fields.Add(largest);

            // private Dictionary<TName, TNode> McvsIndexFor(JObject jo)
            // { int n = jo.Count; if (n < 8) return null; mcvsIndexed++; if (n > mcvsLargest) mcvsLargest = n; return new Dictionary<TName, TNode>(); }
            var indexFor = new MethodDefinition("McvsIndexFor", MethodAttributes.Private | MethodAttributes.HideBySig, index);
            indexFor.Parameters.Add(new ParameterDefinition("jo", ParameterAttributes.None, jobject));
            indexFor.Body.Variables.Add(new VariableDefinition(ts.Int32));
            indexFor.Body.InitLocals = true;
            {
                ILProcessor il = indexFor.Body.GetILProcessor();
                Instruction make = il.Create(OpCodes.Ldarg_0);
                Instruction create = il.Create(OpCodes.Newobj, newIndex);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, module.ImportReference(getCount));
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldc_I4, IndexMinProperties);
                il.Emit(OpCodes.Bge_S, make);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
                il.Append(make);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, indexed);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stfld, indexed);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, largest);
                il.Emit(OpCodes.Ble_S, create);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Stfld, largest);
                il.Append(create);
                il.Emit(OpCodes.Ret);
            }
            reader.Methods.Add(indexFor);

            // private void McvsBind(TNode parent, TNode child, Dictionary<TName, TNode> index): see the summary.
            var bind = new MethodDefinition("McvsBind", MethodAttributes.Private | MethodAttributes.HideBySig, ts.Void);
            bind.Parameters.Add(new ParameterDefinition("parent", ParameterAttributes.None, node));
            bind.Parameters.Add(new ParameterDefinition("child", ParameterAttributes.None, node));
            bind.Parameters.Add(new ParameterDefinition("index", ParameterAttributes.None, index));
            var childName = new VariableDefinition(name);
            var found = new VariableDefinition(node);
            bind.Body.Variables.Add(childName);
            bind.Body.Variables.Add(found);
            bind.Body.InitLocals = true;
            {
                ILProcessor il = bind.Body.GetILProcessor();
                Instruction shipped = il.Create(OpCodes.Ldarg_0);
                Instruction add = il.Create(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Brfalse_S, shipped);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Callvirt, nodeGetName);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloca_S, childName);
                il.Emit(OpCodes.Call, nameGetName);
                il.Emit(OpCodes.Brfalse_S, shipped);
                il.Emit(OpCodes.Ldloca_S, childName);
                il.Emit(OpCodes.Call, nameGetType);
                il.Emit(OpCodes.Ldc_I4, AttributeType);
                il.Emit(OpCodes.Beq_S, shipped);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldloca_S, found);
                il.Emit(OpCodes.Callvirt, tryGetValue);
                il.Emit(OpCodes.Brfalse_S, add);
                il.Emit(OpCodes.Ldloc_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Callvirt, nodeMergeWith);
                il.Emit(OpCodes.Ret);
                il.Append(add);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Callvirt, nodeAdd);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Callvirt, indexAdd);
                il.Emit(OpCodes.Ret);
                il.Append(shipped);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, bindNodes);
                il.Emit(OpCodes.Ret);
            }
            reader.Methods.Add(bind);

            // ---- change: ReadObject indexes its object's children
            {
                MethodBody body = readObject.Body;
                ILProcessor il = body.GetILProcessor();
                var local = new VariableDefinition(index);
                body.Variables.Add(local);
                Instruction first = body.Instructions[0];
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_2));
                il.InsertBefore(first, il.Create(OpCodes.Call, indexFor));
                il.InsertBefore(first, il.Create(OpCodes.Stloc_S, local));
                il.InsertBefore(binds[0], il.Create(OpCodes.Ldloc_S, local));
                binds[0].Operand = bind;
                PatchSites.FixShortBranches(readObject);
            }
            // ---- change: ReadArray adds without the conflict scan
            adds[0].Previous.OpCode = OpCodes.Ldc_I4_0;
            context.Notes.Add(readObject.FullName + ": a JSON object of " + IndexMinProperties + " properties or more finds its children with a " +
                              "dictionary (McvsBind) instead of HasName + Add's conflict scan per property, the same tree; " + readArray.Name +
                              " adds each element without the conflict scan ([Count] is always new); the unlock-all profile's 11,368-key " +
                              "Inventory took ~129 million name comparisons per read (every bot at agent select, every profile load)");

            // ---- change: timing of a read (ReadDefaultTemplate, LoadRoot) and of TObject.FromObject
            {
                MethodBody body = readDefault.Body;
                ILProcessor il = body.GetILProcessor();
                var started = new VariableDefinition(ts.Int64);
                body.Variables.Add(started);
                body.InitLocals = true;
                Instruction callLoad = defaultCode[3];
                Instruction after = defaultCode[5];
                il.InsertBefore(callLoad, il.Create(OpCodes.Call, clock));
                il.InsertBefore(callLoad, il.Create(OpCodes.Stloc_0));
                foreach (Instruction i in new[]
                         {
                             il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, sourceName), il.Create(OpCodes.Ldloc_0),
                             il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, rooted),
                             il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, indexed),
                             il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, largest),
                             il.Create(OpCodes.Call, read),
                         })
                    il.InsertBefore(after, i);
                PatchSites.FixShortBranches(readDefault);
            }
            {
                ILProcessor il = loadRoot.Body.GetILProcessor();
                foreach (Instruction ret in rootReturns)
                {
                    il.InsertBefore(ret, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(ret, il.Create(OpCodes.Call, clock));
                    il.InsertBefore(ret, il.Create(OpCodes.Stfld, rooted));
                }
                PatchSites.FixShortBranches(loadRoot);
            }
            {
                MethodBody body = fromObject.Body;
                ILProcessor il = body.GetILProcessor();
                body.Variables.Add(new VariableDefinition(ts.Int64));
                body.InitLocals = true;
                Instruction first = fromCode[0], ret = fromCode[4];
                il.InsertBefore(first, il.Create(OpCodes.Call, clock));
                il.InsertBefore(first, il.Create(OpCodes.Stloc_0));
                il.InsertBefore(ret, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(ret, il.Create(OpCodes.Ldloc_0));
                il.InsertBefore(ret, il.Create(OpCodes.Call, converted));
            }
            context.Notes.Add(readDefault.FullName + ", " + loadRoot.Name + " and " + fromObject.FullName + ": their times go to " + HooksType +
                              " (a 'templates:' line for a read or a FromObject of 100 ms or more)");

            ApplyTrees(module, context, trees, clock, jsonDone);
        }

        // ------------------------------------------------------------------------------------------------ writer, Copy, MergeWith

        /// <summary>What <see cref="VerifyTrees"/> found, for <see cref="ApplyTrees"/>.</summary>
        private sealed class TreeSites
        {
            public TypeDefinition Node, Object, Array, Value;
            public MethodDefinition Convert, Copy, MergeWith, ToJson, Add, GetName, NameGetName, NodeCopy, SetNode;
            public Instruction ConvertAdd, ConvertCreated, CopyAdd, MergeStart, JsonStart, JsonReturn;
            public FieldDefinition Nodes, Name, Parent;
            public MethodReference ListCount, ListItem, ObjectGetType, TypeEquals, GetValue;
        }

        /// <summary>
        /// Checks every site of the writer, Copy and MergeWith changes and the methods their argument reads (see the class summary of
        /// <see cref="ApplyTrees"/>), before anything is changed.
        /// </summary>
        private TreeSites VerifyTrees(ModuleDefinition module, PatchContext context, TypeDefinition node, TypeDefinition tobject,
                                      MethodDefinition nodeAdd, MethodDefinition nodeMergeWith, MethodDefinition nodeGetName, MethodDefinition nameGetName)
        {
            var t = new TreeSites { Node = node, Object = tobject, Array = RequireType(module, ArrayType), Value = RequireType(module, ValueType),
                                    MergeWith = nodeMergeWith, Add = nodeAdd, GetName = nodeGetName, NameGetName = nameGetName };
            TypeDefinition converter = RequireType(module, ConverterType);
            t.Convert = RequireMethod(converter, "ConvertToTemplate",
                m => !m.IsStatic && m.IsVirtual && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.MetadataType == MetadataType.Object &&
                     m.ReturnType.FullName == NodeType, "virtual TNode ConvertToTemplate(object, TemplateContext)");
            RequireFingerprint(t.Convert, ForBuild(context, ConvertToTemplateFingerprint, PcConvertToTemplateFingerprint), "DefaultConverter.ConvertToTemplate");
            t.Copy = RequireMethod(node, "Copy", m => !m.IsStatic && !m.IsVirtual && m.Parameters.Count == 0 && m.ReturnType.FullName == NodeType, "TNode Copy()");
            RequireFingerprint(t.Copy, ForBuild(context, CopyFingerprint, PcCopyFingerprint), "TNode.Copy");
            t.ToJson = RequireMethod(tobject, "ToString", m => !m.IsStatic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
                                                               m.ReturnType.MetadataType == MetadataType.String, "string ToString(string)");
            RequireFingerprint(t.ToJson, ForBuild(context, ToStringFingerprint, PcToStringFingerprint), "TObject.ToString(string)");
            List<MethodDefinition> trees = TreesReliedOn(module);
            string combined = Combined(trees);
            if (combined != ForBuild(context, TreesReliedOnFingerprint, PcTreesReliedOnFingerprint))
            {
                throw new PatchException("Gameloft.Templates.dll: the methods the writer/Copy/MergeWith change relies on are not the verified code (combined fingerprint " +
                                         combined + "):\n" + string.Join("\n", trees.Select(m => "    " + m.FullName + " " + Fingerprint(m))));
            }
            t.NodeCopy = t.Copy;
            t.SetNode = trees[1];
            // TObject and TValue keep TNode's Add, MergeWith and Copy (the exact-type checks below rely on it).
            foreach (TypeDefinition type in new[] { tobject, t.Value })
                if (type.Methods.Any(m => m.Name == "Add" || (type == tobject && m.Name == "MergeWith") || m.Name == "Copy" || m.Name == "CopyNode"))
                    throw new PatchException(type.FullName + " declares its own Add, MergeWith or Copy");
            if (!node.IsAbstract) throw new PatchException(NodeType + " is not abstract");
            foreach (string member in new[] { "McvsAddNew", "McvsConvertBegin", "McvsConvertAdd", "McvsCopyAdd", "McvsMergeFrom", "mcvsShared" })
                if (node.Methods.Any(m => m.Name == member) || node.Fields.Any(f => f.Name == member)) throw new PatchException(NodeType + " already has " + member);
            t.Nodes = RequireField(node, "nodes", "System.Collections.Generic.List`1<" + NodeType + ">");
            t.Name = RequireField(node, "name", NameType);
            t.Parent = RequireField(node, "parent", NodeType);

            // ConvertToTemplate: "stloc.2" of CreateTemplateNode, then exactly one "ldloc.2; ldloc.s V_6; ldc.i4.1; callvirt Add".
            var code = t.Convert.Body.Instructions;
            List<Instruction> adds = code.Where(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference m && m.Resolve() == nodeAdd).ToList();
            List<Instruction> created = code.Where(i => i.Operand is MethodReference m && m.Name == "CreateTemplateNode").ToList();
            if (adds.Count != 1 || adds[0].Previous.OpCode != OpCodes.Ldc_I4_1 || adds[0].Previous.Previous.OpCode != OpCodes.Ldloc_S ||
                adds[0].Previous.Previous.Previous.OpCode != OpCodes.Ldloc_2 || created.Count != 1 || created[0].Next.OpCode != OpCodes.Stloc_2 ||
                PatchSites.BranchesTo(t.Convert.Body, created[0].Next.Next).Count != 0 || t.Convert.Body.Variables.Count != 9 ||
                t.Convert.Body.Variables[2].VariableType.FullName != NodeType)
                throw new PatchException(t.Convert.FullName + ": the Add call is not where the fingerprint says:\n" + Dump(t.Convert));
            t.ConvertAdd = adds[0];
            t.ConvertCreated = created[0].Next;
            // Copy: exactly one "callvirt Copy; ldc.i4.1; callvirt Add".
            List<Instruction> copyAdds = t.Copy.Body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference m && m.Resolve() == nodeAdd).ToList();
            if (copyAdds.Count != 1 || copyAdds[0].Previous.OpCode != OpCodes.Ldc_I4_1 ||
                !(copyAdds[0].Previous.Previous.Operand is MethodReference cm && cm.Resolve() == t.Copy) || t.Copy.Body.Variables.Count != 2)
                throw new PatchException(t.Copy.FullName + ": the Add call is not where the fingerprint says:\n" + Dump(t.Copy));
            t.CopyAdd = copyAdds[0];
            // MergeWith: "ldloc.0; brfalse.s END; ldc.i4.0; stloc.3" starts the loop; its operands give List's Count/Item and GetValue<object>.
            var merge = nodeMergeWith.Body.Instructions;
            List<Instruction> starts = merge.Where(i => i.OpCode == OpCodes.Ldc_I4_0 && i.Next.OpCode == OpCodes.Stloc_3 && i.Previous.OpCode == OpCodes.Brfalse_S &&
                                                        i.Previous.Previous.OpCode == OpCodes.Ldloc_0).ToList();
            List<Instruction> values = merge.Where(i => i.OpCode == OpCodes.Call && i.Operand is GenericInstanceMethod g && g.Name == "GetValue" &&
                                                        g.GenericArguments.Count == 1 && g.GenericArguments[0].MetadataType == MetadataType.Object).ToList();
            List<Instruction> counts = merge.Where(i => i.Operand is MethodReference m && m.Name == "get_Count" && m.DeclaringType.FullName.StartsWith("System.Collections.Generic.List`1", System.StringComparison.Ordinal)).ToList();
            List<Instruction> items = merge.Where(i => i.Operand is MethodReference m && m.Name == "get_Item" && m.DeclaringType.FullName.StartsWith("System.Collections.Generic.List`1", System.StringComparison.Ordinal)).ToList();
            if (starts.Count != 1 || values.Count != 1 || counts.Count != 1 || items.Count != 1 || nodeMergeWith.Body.Variables.Count != 7 ||
                nodeMergeWith.Body.Variables[0].VariableType.FullName != "System.Collections.Generic.List`1<" + NodeType + ">")
                throw new PatchException(nodeMergeWith.FullName + ": the loop is not where the fingerprint says:\n" + Dump(nodeMergeWith));
            t.MergeStart = starts[0];
            t.GetValue = (MethodReference)values[0].Operand;
            t.ListCount = (MethodReference)counts[0].Operand;
            t.ListItem = (MethodReference)items[0].Operand;
            // Object::GetType and Type::op_Equality, as the converter calls them.
            List<Instruction> getTypes = code.Where(i => i.Operand is MethodReference m && m.Name == "GetType" && m.DeclaringType.MetadataType == MetadataType.Object && m.Parameters.Count == 0).ToList();
            List<Instruction> equals = code.Where(i => i.Operand is MethodReference m && m.Name == "op_Equality" && m.DeclaringType.FullName == "System.Type").ToList();
            if (getTypes.Count != 1 || equals.Count != 1)
                throw new PatchException(t.Convert.FullName + ": no Object::GetType / Type::op_Equality call to copy:\n" + Dump(t.Convert));
            t.ObjectGetType = (MethodReference)getTypes[0].Operand;
            t.TypeEquals = (MethodReference)equals[0].Operand;
            // TObject.ToString(string): "newobj MemoryStream::.ctor" starts the J branch; "ldloc.3; ret" ends it.
            var json = t.ToJson.Body.Instructions;
            List<Instruction> streams = json.Where(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference m && m.DeclaringType.FullName == "System.IO.MemoryStream").ToList();
            List<Instruction> rets = json.Where(i => i.OpCode == OpCodes.Ret).ToList();
            if (streams.Count != 1 || rets.Count != 2 || rets[1].Previous.OpCode != OpCodes.Ldloc_3 || t.ToJson.Body.Variables.Count != 4 ||
                t.ToJson.Body.Variables[3].VariableType.MetadataType != MetadataType.String || t.ToJson.Body.ExceptionHandlers.Count != 1 ||
                PatchSites.BranchesTo(t.ToJson.Body, rets[1]).Count != 0)
                throw new PatchException(t.ToJson.FullName + ": not the verified body:\n" + Dump(t.ToJson));
            t.JsonStart = streams[0];
            t.JsonReturn = rets[1];
            return t;
        }

        /// <summary>
        /// The writer (<c>DefaultConverter.ConvertToTemplate</c>), <c>TNode.Copy</c> and <c>TNode.MergeWith</c> without their
        /// per-child name scans, and the time of <c>TObject.ToString("j")</c>:
        /// <list type="bullet">
        /// <item><c>ConvertToTemplate</c> adds each child with <c>McvsConvertAdd(tNode, child, names, eligible)</c> instead of
        /// <c>tNode.Add(child)</c>. <c>eligible</c> (<c>McvsConvertBegin</c>, right after <c>CreateTemplateNode</c>): the new node is
        /// exactly a <c>TObject</c> (whose <c>Add</c> is <c>TNode.Add</c>) with no child yet. From its 8th child on, a
        /// <c>HashSet&lt;TName&gt;</c> holds the names of its children (built once from them, then kept): a name not in the set is
        /// added with <c>Add(child, false)</c>, a name in it (a duplicate: the game's check throws) and a null name with the game's
        /// <c>Add(child)</c>. Why the set is exact: the node is the method's own local, every child came through this call, and every
        /// converter of the game (Templates, Engine and Game, both builds) returns a node it has just created and keeps no
        /// reference to it, so nothing else can change the node's children or their names. A child that already has a parent (a
        /// converter that returns a node of another tree: none of the game's does) turns the fast path off for good, for every
        /// conversion (<c>mcvsShared</c>).</item>
        /// <item><c>Copy</c> adds each child copy with <c>McvsCopyAdd</c>, the same set from the 8th child on when the copy is exactly
        /// a <c>TObject</c>: the copy is new (<c>CopyNode</c> sets its child list to null) and each child is a new copy nothing else
        /// holds, and copying the next child only reads the source.</item>
        /// <item><c>MergeWith</c> starts its loop at <c>McvsMergeFrom(list)</c> instead of 0. When this node is exactly a
        /// <c>TObject</c> whose names are all different, the source has 8 children or more and is not this node,
        /// <c>McvsMergeFrom</c> runs the game's loop with a <c>Dictionary&lt;TName, TNode&gt;</c> of this node's children in place
        /// of <c>Node(name)</c> and <c>Add(copy, false)</c> in place of <c>Add(copy)</c> (the name is not there), keeping the
        /// dictionary with each of its own changes (an added copy, a <c>SetNode</c> that replaces a null value). After each merge
        /// into a child (only TObject, TArray and TValue: the only TNode types of the game) it checks that this node's child list is
        /// the same list with the same count and the same last child; anything else (only possible if the tree is not a tree: a
        /// node in two lists, or its own descendant) and a null name hand the loop back to the game at the next element, on the
        /// state as it is. Names of this node's children never change during the loop (a merge sets a child's name to the equal name
        /// it was found by).</item>
        /// <item><c>TObject.ToString("j"/"J")</c> reports its time (<c>TemplateToJson</c>; the profile save's text).</item>
        /// </list>
        /// </summary>
        private static void ApplyTrees(ModuleDefinition module, PatchContext context, TreeSites t, MethodDefinition clock, MethodDefinition jsonDone)
        {
            TypeSystem ts = module.TypeSystem;
            TypeDefinition node = t.Node;
            TypeReference name = t.Name.FieldType;
            TypeReference list = t.Nodes.FieldType;
            // HashSet<TName> from the module's own reference (TNode.ExtendedNodeNames returns one).
            MethodDefinition extendedNames = node.Methods.Single(m => m.Name == "ExtendedNodeNames" && m.Parameters.Count == 0);
            var setType = extendedNames.ReturnType as GenericInstanceType;
            if (setType == null || setType.ElementType.FullName != "System.Collections.Generic.HashSet`1")
                throw new PatchException(extendedNames.FullName + " does not return a HashSet<TName>");
            TypeReference openSet = setType.ElementType;
            var newSet = new MethodReference(".ctor", ts.Void, setType) { HasThis = true };
            var setAdd = new MethodReference("Add", ts.Boolean, setType) { HasThis = true };
            setAdd.Parameters.Add(new ParameterDefinition(openSet.GenericParameters[0]));
            var setContains = new MethodReference("Contains", ts.Boolean, setType) { HasThis = true };
            setContains.Parameters.Add(new ParameterDefinition(openSet.GenericParameters[0]));
            foreach (MethodReference m in new[] { newSet, setAdd, setContains })
                if (m.Resolve() == null) throw new PatchException("the game's HashSet`1 has no " + m.FullName);
            // Dictionary<TName, TNode>
            var openDict = new TypeReference("System.Collections.Generic", "Dictionary`2", module, ts.CoreLibrary);
            openDict.GenericParameters.Add(new GenericParameter("TKey", openDict));
            openDict.GenericParameters.Add(new GenericParameter("TValue", openDict));
            var dict = new GenericInstanceType(openDict);
            dict.GenericArguments.Add(name);
            dict.GenericArguments.Add(node);
            var newDict = new MethodReference(".ctor", ts.Void, dict) { HasThis = true };
            var dictTryGet = new MethodReference("TryGetValue", ts.Boolean, dict) { HasThis = true };
            dictTryGet.Parameters.Add(new ParameterDefinition(openDict.GenericParameters[0]));
            dictTryGet.Parameters.Add(new ParameterDefinition(new ByReferenceType(openDict.GenericParameters[1])));
            var dictAdd = new MethodReference("Add", ts.Void, dict) { HasThis = true };
            dictAdd.Parameters.Add(new ParameterDefinition(openDict.GenericParameters[0]));
            dictAdd.Parameters.Add(new ParameterDefinition(openDict.GenericParameters[1]));
            var dictSet = new MethodReference("set_Item", ts.Void, dict) { HasThis = true };
            dictSet.Parameters.Add(new ParameterDefinition(openDict.GenericParameters[0]));
            dictSet.Parameters.Add(new ParameterDefinition(openDict.GenericParameters[1]));
            var dictHas = new MethodReference("ContainsKey", ts.Boolean, dict) { HasThis = true };
            dictHas.Parameters.Add(new ParameterDefinition(openDict.GenericParameters[0]));
            foreach (MethodReference m in new[] { newDict, dictTryGet, dictAdd, dictSet, dictHas })
                if (m.Resolve() == null) throw new PatchException("the game's mscorlib has no " + m.FullName);
            TypeReference typeType = t.TypeEquals.Parameters[0].ParameterType;
            MethodReference fromHandle = CorlibMethod(module, typeType, "GetTypeFromHandle", false, typeType, PatchSites.CorlibType(module, "System", "RuntimeTypeHandle"));

            var shared = new FieldDefinition("mcvsShared", FieldAttributes.Private | FieldAttributes.Static, ts.Boolean);
            node.Fields.Add(shared);

            // "ldarg/ldloc x; callvirt GetType; ldtoken T; call GetTypeFromHandle; call op_Equality" -> bool on the stack
            System.Action<ILProcessor, Instruction, TypeReference> isExactly = (il, load, type) =>
            {
                il.Append(load);
                il.Emit(OpCodes.Callvirt, t.ObjectGetType);
                il.Emit(OpCodes.Ldtoken, type);
                il.Emit(OpCodes.Call, fromHandle);
                il.Emit(OpCodes.Call, t.TypeEquals);
            };

            // internal static HashSet<TName> McvsAddNew(TNode parent, TNode child, HashSet<TName> names)
            // {
            //     TName n = child.name;
            //     if (n.Name == null) { parent.Add(child, true); return names; }
            //     if (names == null)
            //     {
            //         List<TNode> l = parent.nodes;
            //         if (l == null || l.Count < 7) { parent.Add(child, true); return null; }
            //         names = new HashSet<TName>();
            //         for (int k = 0; k < l.Count; k++) { TName c = l[k].name; if (c.Name != null) names.Add(c); }
            //     }
            //     if (names.Contains(n)) { parent.Add(child, true); names.Add(n); return names; }   // the game's check throws
            //     parent.Add(child, false); names.Add(n); return names;
            // }
            var addNew = new MethodDefinition("McvsAddNew", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, setType);
            addNew.Parameters.Add(new ParameterDefinition("parent", ParameterAttributes.None, node));
            addNew.Parameters.Add(new ParameterDefinition("child", ParameterAttributes.None, node));
            addNew.Parameters.Add(new ParameterDefinition("names", ParameterAttributes.None, setType));
            var aN = new VariableDefinition(name);
            var aL = new VariableDefinition(list);
            var aK = new VariableDefinition(ts.Int32);
            var aC = new VariableDefinition(name);
            foreach (var v in new[] { aN, aL, aK, aC }) addNew.Body.Variables.Add(v);
            addNew.Body.InitLocals = true;
            {
                ILProcessor il = addNew.Body.GetILProcessor();
                Instruction named = il.Create(OpCodes.Ldarg_2), small = il.Create(OpCodes.Ldarg_0), build = il.Create(OpCodes.Newobj, newSet),
                            loop = il.Create(OpCodes.Ldloc_1), next = il.Create(OpCodes.Ldloc_2), cond = il.Create(OpCodes.Ldloc_2),
                            have = il.Create(OpCodes.Ldarg_2), fresh = il.Create(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldfld, t.Name);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloca_S, aN);
                il.Emit(OpCodes.Call, t.NameGetName);
                il.Emit(OpCodes.Brtrue, named);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Callvirt, t.Add);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ret);
                il.Append(named);                       // ldarg.2
                il.Emit(OpCodes.Brtrue, have);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, t.Nodes);
                il.Emit(OpCodes.Stloc_1);
                il.Emit(OpCodes.Ldloc_1);
                il.Emit(OpCodes.Brfalse, small);
                il.Emit(OpCodes.Ldloc_1);
                il.Emit(OpCodes.Callvirt, t.ListCount);
                il.Emit(OpCodes.Ldc_I4, IndexMinProperties - 1);
                il.Emit(OpCodes.Bge, build);
                il.Append(small);                       // ldarg.0
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Callvirt, t.Add);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
                il.Append(build);                       // newobj HashSet
                il.Emit(OpCodes.Starg_S, addNew.Parameters[2]);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stloc_2);
                il.Emit(OpCodes.Br, cond);
                il.Append(loop);                        // ldloc.1
                il.Emit(OpCodes.Ldloc_2);
                il.Emit(OpCodes.Callvirt, t.ListItem);
                il.Emit(OpCodes.Ldfld, t.Name);
                il.Emit(OpCodes.Stloc_3);
                il.Emit(OpCodes.Ldloca_S, aC);
                il.Emit(OpCodes.Call, t.NameGetName);
                il.Emit(OpCodes.Brfalse, next);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldloc_3);
                il.Emit(OpCodes.Callvirt, setAdd);
                il.Emit(OpCodes.Pop);
                il.Append(next);                        // ldloc.2
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc_2);
                il.Append(cond);                        // ldloc.2
                il.Emit(OpCodes.Ldloc_1);
                il.Emit(OpCodes.Callvirt, t.ListCount);
                il.Emit(OpCodes.Blt, loop);
                il.Append(have);                        // ldarg.2
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Callvirt, setContains);
                il.Emit(OpCodes.Brfalse, fresh);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Callvirt, t.Add);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Callvirt, setAdd);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ret);
                il.Append(fresh);                       // ldarg.0
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Callvirt, t.Add);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Callvirt, setAdd);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ret);
            }
            node.Methods.Add(addNew);

            // internal static bool McvsConvertBegin(TNode parent)
            // { return !mcvsShared && parent != null && parent.GetType() == typeof(TObject) && (parent.nodes == null || parent.nodes.Count == 0); }
            var begin = new MethodDefinition("McvsConvertBegin", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Boolean);
            begin.Parameters.Add(new ParameterDefinition("parent", ParameterAttributes.None, node));
            {
                ILProcessor il = begin.Body.GetILProcessor();
                Instruction no = il.Create(OpCodes.Ldc_I4_0), yes = il.Create(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ldsfld, shared);
                il.Emit(OpCodes.Brtrue, no);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Brfalse, no);
                isExactly(il, il.Create(OpCodes.Ldarg_0), t.Object);
                il.Emit(OpCodes.Brfalse, no);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, t.Nodes);
                il.Emit(OpCodes.Brfalse, yes);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, t.Nodes);
                il.Emit(OpCodes.Callvirt, t.ListCount);
                il.Emit(OpCodes.Brtrue, no);
                il.Append(yes);
                il.Emit(OpCodes.Ret);
                il.Append(no);
                il.Emit(OpCodes.Ret);
            }
            node.Methods.Add(begin);

            // internal static HashSet<TName> McvsConvertAdd(TNode parent, TNode child, HashSet<TName> names, bool eligible)
            // {
            //     if (eligible && !mcvsShared && child != null)
            //     {
            //         if (child.parent == null) return McvsAddNew(parent, child, names);
            //         mcvsShared = true;     // a node of another tree: the game's checks from now on, everywhere
            //     }
            //     parent.Add(child, true); return null;
            // }
            var convertAdd = new MethodDefinition("McvsConvertAdd", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, setType);
            convertAdd.Parameters.Add(new ParameterDefinition("parent", ParameterAttributes.None, node));
            convertAdd.Parameters.Add(new ParameterDefinition("child", ParameterAttributes.None, node));
            convertAdd.Parameters.Add(new ParameterDefinition("names", ParameterAttributes.None, setType));
            convertAdd.Parameters.Add(new ParameterDefinition("eligible", ParameterAttributes.None, ts.Boolean));
            {
                ILProcessor il = convertAdd.Body.GetILProcessor();
                Instruction shipped = il.Create(OpCodes.Ldarg_0), fresh = il.Create(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Brfalse, shipped);
                il.Emit(OpCodes.Ldsfld, shared);
                il.Emit(OpCodes.Brtrue, shipped);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Brfalse, shipped);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldfld, t.Parent);
                il.Emit(OpCodes.Brfalse, fresh);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Stsfld, shared);
                il.Emit(OpCodes.Br, shipped);
                il.Append(fresh);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, addNew);
                il.Emit(OpCodes.Ret);
                il.Append(shipped);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Callvirt, t.Add);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
            }
            node.Methods.Add(convertAdd);

            // internal static HashSet<TName> McvsCopyAdd(TNode parent, TNode child, HashSet<TName> names)
            // { if (parent.GetType() == typeof(TObject)) return McvsAddNew(parent, child, names); parent.Add(child, true); return null; }
            var copyAdd = new MethodDefinition("McvsCopyAdd", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, setType);
            copyAdd.Parameters.Add(new ParameterDefinition("parent", ParameterAttributes.None, node));
            copyAdd.Parameters.Add(new ParameterDefinition("child", ParameterAttributes.None, node));
            copyAdd.Parameters.Add(new ParameterDefinition("names", ParameterAttributes.None, setType));
            {
                ILProcessor il = copyAdd.Body.GetILProcessor();
                Instruction shipped = il.Create(OpCodes.Ldarg_0);
                isExactly(il, il.Create(OpCodes.Ldarg_0), t.Object);
                il.Emit(OpCodes.Brfalse, shipped);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, addNew);
                il.Emit(OpCodes.Ret);
                il.Append(shipped);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Callvirt, t.Add);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
            }
            node.Methods.Add(copyAdd);

            MethodDefinition mergeFrom = MergeFrom(module, t, name, list, dict, newDict, dictTryGet, dictAdd, dictSet, dictHas, isExactly);
            node.Methods.Add(mergeFrom);

            // ---- change: ConvertToTemplate
            {
                MethodBody body = t.Convert.Body;
                ILProcessor il = body.GetILProcessor();
                var names = new VariableDefinition(setType);
                var eligible = new VariableDefinition(ts.Boolean);
                body.Variables.Add(names);
                body.Variables.Add(eligible);
                body.InitLocals = true;
                Instruction after = t.ConvertCreated;   // stloc.2
                il.InsertAfter(after, il.Create(OpCodes.Stloc_S, eligible));
                il.InsertAfter(after, il.Create(OpCodes.Call, begin));
                il.InsertAfter(after, il.Create(OpCodes.Ldloc_2));
                il.InsertAfter(after, il.Create(OpCodes.Stloc_S, names));
                il.InsertAfter(after, il.Create(OpCodes.Ldnull));
                Instruction flag = t.ConvertAdd.Previous;   // ldc.i4.1
                flag.OpCode = OpCodes.Ldloc_S;
                flag.Operand = names;
                il.InsertBefore(t.ConvertAdd, il.Create(OpCodes.Ldloc_S, eligible));
                t.ConvertAdd.OpCode = OpCodes.Call;
                t.ConvertAdd.Operand = convertAdd;
                il.InsertAfter(t.ConvertAdd, il.Create(OpCodes.Stloc_S, names));
                PatchSites.FixShortBranches(t.Convert);
            }
            // ---- change: Copy
            {
                MethodBody body = t.Copy.Body;
                ILProcessor il = body.GetILProcessor();
                var names = new VariableDefinition(setType);
                body.Variables.Add(names);
                body.InitLocals = true;
                Instruction flag = t.CopyAdd.Previous;      // ldc.i4.1
                flag.OpCode = OpCodes.Ldloc_S;
                flag.Operand = names;
                t.CopyAdd.OpCode = OpCodes.Call;
                t.CopyAdd.Operand = copyAdd;
                il.InsertAfter(t.CopyAdd, il.Create(OpCodes.Stloc_S, names));
                PatchSites.FixShortBranches(t.Copy);
            }
            // ---- change: MergeWith's loop starts where McvsMergeFrom stopped
            {
                ILProcessor il = t.MergeWith.Body.GetILProcessor();
                Instruction start = t.MergeStart;            // ldc.i4.0
                start.OpCode = OpCodes.Ldarg_0;
                start.Operand = null;
                il.InsertAfter(start, il.Create(OpCodes.Call, mergeFrom));
                il.InsertAfter(start, il.Create(OpCodes.Ldloc_0));
                PatchSites.FixShortBranches(t.MergeWith);
            }
            context.Notes.Add(t.Convert.FullName + ", " + node.FullName + "::Copy and ::MergeWith: a TObject finds its children's names with a set (a " +
                              "dictionary in MergeWith) from its 8th child instead of the conflict scan per child; the same tree, the same exceptions " +
                              "(the unlock-all Inventory: ~65 million name comparisons per profile save, as many per profile load's merge)");

            // ---- change: the time of ToString("j")
            {
                MethodBody body = t.ToJson.Body;
                ILProcessor il = body.GetILProcessor();
                var started = new VariableDefinition(ts.Int64);
                body.Variables.Add(started);
                body.InitLocals = true;
                Instruction stream = t.JsonStart;
                Instruction first = il.Create(OpCodes.Call, clock);
                il.InsertBefore(stream, first);
                il.InsertBefore(stream, il.Create(OpCodes.Stloc_S, started));
                foreach (Instruction b in body.Instructions)
                    if (b != first && b.Operand == stream) b.Operand = first;
                il.InsertBefore(t.JsonReturn, il.Create(OpCodes.Dup));
                il.InsertBefore(t.JsonReturn, il.Create(OpCodes.Ldloc_S, started));
                il.InsertBefore(t.JsonReturn, il.Create(OpCodes.Call, jsonDone));
                PatchSites.FixShortBranches(t.ToJson);
            }
            context.Notes.Add(t.ToJson.FullName + ": the time of a \"j\"/\"J\" text goes to " + HooksType + " (a 'templates:' line of 100 ms or more)");
        }

        /// <summary>
        /// <c>private int McvsMergeFrom(List&lt;TNode&gt; list)</c>: the element index the game's MergeWith loop continues at (see
        /// <see cref="ApplyTrees"/>).
        /// <code>
        /// if (list.Count &lt; 8 || GetType() != typeof(TObject) || list == nodes) return 0;
        /// var index = new Dictionary&lt;TName, TNode&gt;(); List&lt;TNode&gt; own = nodes; int count = 0; TNode last = null;
        /// if (own != null)
        /// {
        ///     count = own.Count;
        ///     for (int k = 0; k &lt; count; k++) { TNode c = own[k]; TName cn = c.name; if (cn.Name != null) { if (index.ContainsKey(cn)) return 0; index.Add(cn, c); } }
        ///     if (count &gt; 0) last = own[count - 1];
        /// }
        /// for (int i = 0; i &lt; list.Count; i++)
        /// {
        ///     TNode s = list[i]; TName n = s.Name;
        ///     if (n.Name == null) return i;
        ///     TNode t; index.TryGetValue(n, out t);
        ///     if (t == null) { TNode c = s.Copy(); Add(c, false); index.Add(n, c); own = nodes; count++; last = c; }
        ///     else if (t is TValue &amp;&amp; ((TValue)t).GetValue&lt;object&gt;() == null) { TNode c = s.Copy(); SetNode(n, c); index[n] = c; own = nodes; last = c; }
        ///     else
        ///     {
        ///         Type tt = t.GetType();
        ///         if (tt != typeof(TObject) &amp;&amp; tt != typeof(TArray) &amp;&amp; tt != typeof(TValue)) return i;
        ///         t.MergeWith(s);
        ///         if (nodes != own || (own != null &amp;&amp; (own.Count != count || (count &gt; 0 &amp;&amp; own[count - 1] != last)))) return i + 1;
        ///     }
        /// }
        /// return list.Count;   // (i)
        /// </code>
        /// </summary>
        private static MethodDefinition MergeFrom(ModuleDefinition module, TreeSites t, TypeReference name, TypeReference list, TypeReference dict,
                                                  MethodReference newDict, MethodReference dictTryGet, MethodReference dictAdd, MethodReference dictSet,
                                                  MethodReference dictHas, System.Action<ILProcessor, Instruction, TypeReference> isExactly)
        {
            TypeSystem ts = module.TypeSystem;
            var method = new MethodDefinition("McvsMergeFrom", MethodAttributes.Private | MethodAttributes.HideBySig, ts.Int32);
            method.Parameters.Add(new ParameterDefinition("list", ParameterAttributes.None, list));
            var index = new VariableDefinition(dict);       // 0
            var own = new VariableDefinition(list);         // 1
            var count = new VariableDefinition(ts.Int32);   // 2
            var last = new VariableDefinition(t.Node);      // 3
            var k = new VariableDefinition(ts.Int32);       // 4
            var c = new VariableDefinition(t.Node);         // 5
            var cn = new VariableDefinition(name);          // 6
            var i = new VariableDefinition(ts.Int32);       // 7
            var s = new VariableDefinition(t.Node);         // 8
            var n = new VariableDefinition(name);           // 9
            var found = new VariableDefinition(t.Node);     // 10
            var tt = new VariableDefinition(t.TypeEquals.Parameters[0].ParameterType);   // 11
            foreach (var v in new[] { index, own, count, last, k, c, cn, i, s, n, found, tt }) method.Body.Variables.Add(v);
            method.Body.InitLocals = true;
            TypeReference typeType = t.TypeEquals.Parameters[0].ParameterType;
            ILProcessor il = method.Body.GetILProcessor();
            Instruction zero = il.Create(OpCodes.Ldc_I4_0), start = il.Create(OpCodes.Ldc_I4_0), bLoop = il.Create(OpCodes.Ldloc_1),
                        bNext = il.Create(OpCodes.Ldloc_S, k), bCond = il.Create(OpCodes.Ldloc_S, k), bDone = il.Create(OpCodes.Ldloc_2),
                        loop = il.Create(OpCodes.Ldarg_1), hit = il.Create(OpCodes.Ldloc_S, found), mergeInto = il.Create(OpCodes.Ldloc_S, found),
                        known = il.Create(OpCodes.Ldloc_S, found), next = il.Create(OpCodes.Ldloc_S, i), cond = il.Create(OpCodes.Ldloc_S, i),
                        retNext = il.Create(OpCodes.Ldloc_S, i), retI = il.Create(OpCodes.Ldloc_S, i);
            // guards
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, t.ListCount);
            il.Emit(OpCodes.Ldc_I4, IndexMinProperties);
            il.Emit(OpCodes.Blt, zero);
            isExactly(il, il.Create(OpCodes.Ldarg_0), t.Object);
            il.Emit(OpCodes.Brfalse, zero);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, t.Nodes);
            il.Emit(OpCodes.Beq, zero);
            // the index of this node's children
            il.Emit(OpCodes.Newobj, newDict);
            il.Emit(OpCodes.Stloc_0);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, t.Nodes);
            il.Emit(OpCodes.Stloc_1);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc_2);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Stloc_3);
            il.Emit(OpCodes.Ldloc_1);
            il.Emit(OpCodes.Brfalse, start);
            il.Emit(OpCodes.Ldloc_1);
            il.Emit(OpCodes.Callvirt, t.ListCount);
            il.Emit(OpCodes.Stloc_2);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc_S, k);
            il.Emit(OpCodes.Br, bCond);
            il.Append(bLoop);                                // ldloc.1
            il.Emit(OpCodes.Ldloc_S, k);
            il.Emit(OpCodes.Callvirt, t.ListItem);
            il.Emit(OpCodes.Stloc_S, c);
            il.Emit(OpCodes.Ldloc_S, c);
            il.Emit(OpCodes.Ldfld, t.Name);
            il.Emit(OpCodes.Stloc_S, cn);
            il.Emit(OpCodes.Ldloca_S, cn);
            il.Emit(OpCodes.Call, t.NameGetName);
            il.Emit(OpCodes.Brfalse, bNext);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldloc_S, cn);
            il.Emit(OpCodes.Callvirt, dictHas);
            il.Emit(OpCodes.Brtrue, zero);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldloc_S, cn);
            il.Emit(OpCodes.Ldloc_S, c);
            il.Emit(OpCodes.Callvirt, dictAdd);
            il.Append(bNext);                                // ldloc k
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc_S, k);
            il.Append(bCond);                                // ldloc k
            il.Emit(OpCodes.Ldloc_2);
            il.Emit(OpCodes.Blt, bLoop);
            il.Append(bDone);                                // ldloc.2
            il.Emit(OpCodes.Brfalse, start);
            il.Emit(OpCodes.Ldloc_1);
            il.Emit(OpCodes.Ldloc_2);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Callvirt, t.ListItem);
            il.Emit(OpCodes.Stloc_3);
            // the game's loop
            il.Append(start);                                // ldc.i4.0
            il.Emit(OpCodes.Stloc_S, i);
            il.Emit(OpCodes.Br, cond);
            il.Append(loop);                                 // ldarg.1
            il.Emit(OpCodes.Ldloc_S, i);
            il.Emit(OpCodes.Callvirt, t.ListItem);
            il.Emit(OpCodes.Stloc_S, s);
            il.Emit(OpCodes.Ldloc_S, s);
            il.Emit(OpCodes.Callvirt, t.GetName);
            il.Emit(OpCodes.Stloc_S, n);
            il.Emit(OpCodes.Ldloca_S, n);
            il.Emit(OpCodes.Call, t.NameGetName);
            il.Emit(OpCodes.Brfalse, retI);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldloc_S, n);
            il.Emit(OpCodes.Ldloca_S, found);
            il.Emit(OpCodes.Callvirt, dictTryGet);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldloc_S, found);
            il.Emit(OpCodes.Brtrue, hit);
            //   not there: Add(s.Copy(), false)
            il.Emit(OpCodes.Ldloc_S, s);
            il.Emit(OpCodes.Callvirt, t.NodeCopy);
            il.Emit(OpCodes.Stloc_S, c);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc_S, c);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Callvirt, t.Add);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldloc_S, n);
            il.Emit(OpCodes.Ldloc_S, c);
            il.Emit(OpCodes.Callvirt, dictAdd);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, t.Nodes);
            il.Emit(OpCodes.Stloc_1);
            il.Emit(OpCodes.Ldloc_2);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc_2);
            il.Emit(OpCodes.Ldloc_S, c);
            il.Emit(OpCodes.Stloc_3);
            il.Emit(OpCodes.Br, next);
            //   there: a TValue whose value is null -> SetNode(n, s.Copy())
            il.Append(hit);                                  // ldloc found
            il.Emit(OpCodes.Isinst, t.Value);
            il.Emit(OpCodes.Brfalse, mergeInto);
            il.Emit(OpCodes.Ldloc_S, found);
            il.Emit(OpCodes.Isinst, t.Value);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, t.GetValue);
            il.Emit(OpCodes.Brtrue, mergeInto);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc_S, n);
            il.Emit(OpCodes.Ldloc_S, s);
            il.Emit(OpCodes.Callvirt, t.NodeCopy);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Stloc_S, c);
            il.Emit(OpCodes.Call, t.SetNode);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldloc_S, n);
            il.Emit(OpCodes.Ldloc_S, c);
            il.Emit(OpCodes.Callvirt, dictSet);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, t.Nodes);
            il.Emit(OpCodes.Stloc_1);
            il.Emit(OpCodes.Ldloc_S, c);
            il.Emit(OpCodes.Stloc_3);
            il.Emit(OpCodes.Br, next);
            //   there: merge into it (a known node type), then check this node's list
            il.Append(mergeInto);                            // ldloc found
            il.Emit(OpCodes.Callvirt, t.ObjectGetType);
            il.Emit(OpCodes.Stloc_S, tt);
            foreach (TypeReference type in new TypeReference[] { t.Object, t.Array })
            {
                il.Emit(OpCodes.Ldloc_S, tt);
                il.Emit(OpCodes.Ldtoken, type);
                il.Emit(OpCodes.Call, FromHandle(module, typeType));
                il.Emit(OpCodes.Call, t.TypeEquals);
                il.Emit(OpCodes.Brtrue, known);
            }
            il.Emit(OpCodes.Ldloc_S, tt);
            il.Emit(OpCodes.Ldtoken, t.Value);
            il.Emit(OpCodes.Call, FromHandle(module, typeType));
            il.Emit(OpCodes.Call, t.TypeEquals);
            il.Emit(OpCodes.Brfalse, retI);
            il.Append(known);                                // ldloc found
            il.Emit(OpCodes.Ldloc_S, s);
            il.Emit(OpCodes.Callvirt, t.MergeWith);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, t.Nodes);
            il.Emit(OpCodes.Ldloc_1);
            il.Emit(OpCodes.Bne_Un, retNext);
            il.Emit(OpCodes.Ldloc_1);
            il.Emit(OpCodes.Brfalse, next);
            il.Emit(OpCodes.Ldloc_1);
            il.Emit(OpCodes.Callvirt, t.ListCount);
            il.Emit(OpCodes.Ldloc_2);
            il.Emit(OpCodes.Bne_Un, retNext);
            il.Emit(OpCodes.Ldloc_2);
            il.Emit(OpCodes.Brfalse, next);
            il.Emit(OpCodes.Ldloc_1);
            il.Emit(OpCodes.Ldloc_2);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Callvirt, t.ListItem);
            il.Emit(OpCodes.Ldloc_3);
            il.Emit(OpCodes.Bne_Un, retNext);
            il.Append(next);                                 // ldloc i
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc_S, i);
            il.Append(cond);                                 // ldloc i
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, t.ListCount);
            il.Emit(OpCodes.Blt, loop);
            il.Emit(OpCodes.Ldloc_S, i);
            il.Emit(OpCodes.Ret);
            il.Append(retNext);                              // ldloc i
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ret);
            il.Append(retI);                                 // ldloc i
            il.Emit(OpCodes.Ret);
            il.Append(zero);                                 // ldc.i4.0
            il.Emit(OpCodes.Ret);
            return method;
        }

        private static MethodReference FromHandle(ModuleDefinition module, TypeReference typeType)
        {
            return CorlibMethod(module, typeType, "GetTypeFromHandle", false, typeType, PatchSites.CorlibType(module, "System", "RuntimeTypeHandle"));
        }

        /// <summary><c>[NoInlining] private static R name(params) =&gt; target(params);</c>, for any number of parameters.</summary>
        private static MethodDefinition Forwarder(TypeDefinition owner, string name, TypeReference returnType, MethodReference target, params TypeReference[] parameters)
        {
            var method = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, returnType);
            method.ImplAttributes |= MethodImplAttributes.NoInlining;
            for (int i = 0; i < parameters.Length; i++) method.Parameters.Add(new ParameterDefinition("a" + i, ParameterAttributes.None, parameters[i]));
            ILProcessor il = method.Body.GetILProcessor();
            foreach (ParameterDefinition p in method.Parameters) il.Append(Arg(p));
            il.Emit(OpCodes.Call, target);
            il.Emit(OpCodes.Ret);
            owner.Methods.Add(method);
            return method;
        }

        /// <summary>
        /// <c>internal static R name(params) { R r = default; if (!hooksMissing) { try { r = forward(params); } catch { hooksMissing = true; } } return r; }</c>
        /// (void: without r).
        /// </summary>
        private static MethodDefinition Latched(TypeDefinition helper, FieldDefinition hooksMissing, string name, TypeReference returnType,
                                                MethodDefinition forward, params TypeReference[] parameters)
        {
            ModuleDefinition module = helper.Module;
            bool hasResult = returnType.MetadataType != MetadataType.Void;
            var method = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, returnType);
            for (int p = 0; p < parameters.Length; p++) method.Parameters.Add(new ParameterDefinition("a" + p, ParameterAttributes.None, parameters[p]));
            if (hasResult)
            {
                method.Body.Variables.Add(new VariableDefinition(returnType));
                method.Body.InitLocals = true;
            }
            ILProcessor il = method.Body.GetILProcessor();
            Instruction result = hasResult ? il.Create(OpCodes.Ldloc_0) : il.Create(OpCodes.Ret);
            il.Emit(OpCodes.Ldsfld, hooksMissing);
            il.Emit(OpCodes.Brtrue_S, result);
            Instruction tryStart = null;
            foreach (ParameterDefinition p in method.Parameters)
            {
                Instruction load = Arg(p);
                il.Append(load);
                if (tryStart == null) tryStart = load;
            }
            Instruction call = il.Create(OpCodes.Call, forward);
            il.Append(call);
            if (tryStart == null) tryStart = call;
            if (hasResult) il.Emit(OpCodes.Stloc_0);
            il.Emit(OpCodes.Leave_S, result);
            Instruction handler = il.Create(OpCodes.Pop);
            il.Append(handler);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Stsfld, hooksMissing);
            il.Emit(OpCodes.Leave_S, result);
            il.Append(result);
            if (hasResult) il.Emit(OpCodes.Ret);
            method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = result, CatchType = module.TypeSystem.Object,
            });
            helper.Methods.Add(method);
            return method;
        }

        /// <summary>ldarg.0-3, else ldarg.s (a static method's parameters).</summary>
        private static Instruction Arg(ParameterDefinition p)
        {
            return p.Index < 4 ? PatchSites.LoadArgument(p.Index) : Instruction.Create(OpCodes.Ldarg_S, p);
        }
    }
}
