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

        public override string Id => "templates";

        public override string AssemblyFile => "Gameloft.Templates.dll";

        public override string Description => "templates: the template reader finds a JSON object's properties with a dictionary instead of scanning them twice per property (JsonTemplateReader.ReadObject; bots at agent select, profile loads; Gameloft.Templates.dll)";

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
