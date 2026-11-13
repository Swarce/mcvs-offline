using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Default 'crmtables' patch of Gameloft.Engine.dll (build 30; the owner's S22 log: the game thread stopped at every match
    /// start and on every return to the menu while it re-read the game's own design databases).
    /// <para>
    /// <c>Database.Changer</c>'s setter is
    /// <code>
    /// if (changer == value) return;
    /// bool first = changer == null &amp;&amp; value != null;
    /// bool reload = (changer?.TableChangers.Count ?? 0) &gt; 0 || (value?.TableChangers.Count ?? 0) &gt; 0;
    /// changer = value;
    /// if (reload) { if (first) OnTableLoaded(null); else Load(File); }
    /// </code>
    /// and every CRM refresh hands it a NEW <c>CRMDatabaseChanger</c> (<c>CRMComponent.GetChanger</c>), so the reference test
    /// never holds: whenever a database has CRM cells, each refresh reads its whole file again (<c>Load</c>: AssetStream,
    /// <c>Json.LoadObject</c>, <c>Populate</c>, string interning; MCVS_Meta.xlsx is 1.6 MB, 34 MB of garbage per load) and applies
    /// the cells to the fresh rows (<c>OnTableLoaded</c> → <c>ApplyCRMTableChanges</c>, then <c>TablesLoaded</c>). Our CRM
    /// document always carries MCVS_Meta cells, so the GS creation (<c>GameplayCRMComponent.InitGameplayCrmSettingsAsync</c>), the
    /// client's handshake reply (<c>OnClientConnected</c>) and the TS's refresh after the results screen reloaded it every time.
    /// </para>
    /// <para>
    /// The change, before the setter's first instruction:
    /// <code>
    /// if (McvsCrmTables.Same(this, changer, value)) { changer = value; TablesLoaded?.Invoke(this, EventArgs.Empty); return; }
    /// </code>
    /// <c>McvsOffline.GameDataHooks.SameCrmChanges</c> answers true only for two distinct changers that both have table changers
    /// and hold the same cells in the same order (table name; field and index field; cell key, operator and value), with a file
    /// set on the database. The tables then already are the file's rows plus exactly these cells (the previous assignment loaded
    /// or applied them, and the cells of a changer are a function of its contents: ADD and MUL included, applied once), so the
    /// reload would give the same rows; what it ends with, <c>TablesLoaded</c>, still runs, so the typed tables
    /// (<c>TableLoader</c>) and the game's listeners rebuild as before. In every other case (and without McvsOffline.Server.dll:
    /// a <c>hooksMissing</c> latch, one exception) the game's setter runs unchanged.
    /// </para>
    /// <para>
    /// Build 31 (the owner's build-30 log: during agent selection MCVS_Meta.xlsx was parsed three times for every bot the GS
    /// adds, about every 5 s: the new per-user database of the bot, then the client's per-player cells, then the GS cells
    /// again, because in our single process all three Worlds' <c>PerUserDatabase.DatabaseLoaded</c> handlers run on it), the
    /// private <c>Database.Load(string path)</c> becomes
    /// <code>
    /// if (McvsCrmTables.Restore(this, path)) { OnTableLoaded(null); return; }
    /// foreach (...) { ...the game's parse... }
    /// McvsCrmTables.Parsed(this, path);
    /// OnTableLoaded(null);
    /// </code>
    /// <c>GameDataHooks.DatabaseFileParsed</c> keeps a copy of a file's freshly parsed rows (before any CRM cell) the second
    /// time a new database parses it; from then on <c>GameDataHooks.RestoreDatabaseFile</c> gives every <c>Load</c> of that
    /// file new copies of those arrays (the same interned strings and boxed values the parse gives) instead of reading and
    /// parsing it, and the game's own <c>OnTableLoaded</c> applies the CRM cells and raises <c>TablesLoaded</c> as before.
    /// <c>Database.Game_DataChanged</c> (a changed data file, development builds only) first calls
    /// <c>McvsCrmTables.Forget()</c>, so nothing kept outlives its file. Without McvsOffline.Server.dll the same latch makes the
    /// method the game's.
    /// </para>
    /// Every changed method is checked against the IL fingerprint of the verified build first.
    /// </summary>
    public sealed class CrmTablesPatch : IlPatch
    {
        public const string TypeName = "Gameloft.Engine.Database";
        public const string ChangerType = "Gameloft.Engine.CRMDatabaseChanger";
        public const string HelperNamespace = "Gameloft.Engine";
        public const string HelperType = "McvsCrmTables";
        public const string HooksType = "McvsOffline.GameDataHooks";
        public const string SetterFingerprint = "6e201fcb907b841d27c1e98433a7410cb712ba5b4ebf5e9ef91ed2166f594b6b";
        public const string PcSetterFingerprint = SetterFingerprint;
        public const string LoadFingerprint = "51d31a01c5e5c1f7c5f14fac8568a431a7ec069289887c796ca58e94b845f7b9";
        public const string PcLoadFingerprint = LoadFingerprint;
        public const string DataChangedFingerprint = "887ba6c2fcf9816a34ff1c275c562c97bcc4493d5000899a661fbbb857a2b007";
        public const string PcDataChangedFingerprint = DataChangedFingerprint;

        public override string Id => "crmtables";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "CRM tables: a database that gets the same CRM cells again keeps its tables instead of re-reading its file, and a file read before is restored from memory instead of parsed again (Database.Changer, Database.Load; Gameloft.Engine.dll)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify
            PatchSites.RequireBuildAssembly(module, context, Id);
            string fingerprint = ForBuild(context, SetterFingerprint, PcSetterFingerprint);
            TypeDefinition database = RequireType(module, TypeName);
            if (module.GetType(HelperNamespace + "." + HelperType) != null)
                throw new PatchException("Gameloft.Engine.dll is already patched: run the patcher on your original game files");
            MethodDefinition setter = RequireMethod(database, "set_Changer",
                m => !m.IsStatic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == ChangerType &&
                     m.ReturnType.MetadataType == MetadataType.Void,
                "instance void set_Changer(CRMDatabaseChanger)");
            RequireFingerprint(setter, fingerprint, "Database.set_Changer");
            MethodDefinition load = RequireMethod(database, "Load",
                m => !m.IsStatic && m.IsPrivate && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
                     m.ReturnType.MetadataType == MetadataType.Void,
                "private instance void Load(string)");
            RequireFingerprint(load, ForBuild(context, LoadFingerprint, PcLoadFingerprint), "Database.Load");
            MethodDefinition dataChanged = RequireMethod(database, "Game_DataChanged",
                m => !m.IsStatic && m.Parameters.Count == 2 && m.ReturnType.MetadataType == MetadataType.Void,
                "instance void Game_DataChanged(object, DataChangedEventArgs)");
            RequireFingerprint(dataChanged, ForBuild(context, DataChangedFingerprint, PcDataChangedFingerprint), "Database.Game_DataChanged");
            MethodDefinition onTableLoaded = RequireMethod(database, "OnTableLoaded",
                m => !m.IsStatic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.EventArgs" &&
                     m.ReturnType.MetadataType == MetadataType.Void,
                "instance void OnTableLoaded(EventArgs)");
            // Load ends with "ldarg.0; ldnull; call OnTableLoaded; ret", the target of the foreach's leave and the end of its finally.
            var loadCode = load.Body.Instructions;
            Instruction tail = loadCode[loadCode.Count - 4];
            if (tail.OpCode != OpCodes.Ldarg_0 || loadCode[loadCode.Count - 3].OpCode != OpCodes.Ldnull ||
                !(loadCode[loadCode.Count - 2].OpCode == OpCodes.Call && loadCode[loadCode.Count - 2].Operand is MethodReference called &&
                  called.Resolve() == onTableLoaded) ||
                loadCode[loadCode.Count - 1].OpCode != OpCodes.Ret || loadCode.Count(i => i.OpCode == OpCodes.Ret) != 1)
                throw new PatchException(load.FullName + " does not end with OnTableLoaded(null):\n" + Dump(load));
            FieldDefinition changer = RequireField(database, "changer", ChangerType);
            FieldDefinition tablesLoaded = RequireField(database, "TablesLoaded", "System.EventHandler");
            if (changer.IsStatic || tablesLoaded.IsStatic) throw new PatchException(TypeName + ": changer/TablesLoaded are static");

            // ---- references
            TypeSystem ts = module.TypeSystem;
            MethodReference hook = ImportServerMethod(module, context, HooksType, "SameCrmChanges", "System.Boolean",
                                                      "System.Object", "System.Object", "System.Object");
            MethodReference restoreHook = ImportServerMethod(module, context, HooksType, "RestoreDatabaseFile", "System.Boolean",
                                                             "System.Object", "System.String");
            MethodReference parsedHook = ImportServerMethod(module, context, HooksType, "DatabaseFileParsed", "System.Void",
                                                            "System.Object", "System.String");
            MethodReference forgetHook = ImportServerMethod(module, context, HooksType, "ForgetDatabaseFiles", "System.Void");
            TypeReference eventArgs = PatchSites.CorlibType(module, "System", "EventArgs");
            TypeReference eventHandler = PatchSites.CorlibType(module, "System", "EventHandler");
            var empty = new FieldReference("Empty", eventArgs, eventArgs);
            if (empty.Resolve() == null) throw new PatchException("the game's mscorlib has no System.EventArgs::Empty");
            FieldReference emptyField = module.ImportReference(empty);
            MethodReference invoke = CorlibMethod(module, eventHandler, "Invoke", true, ts.Void, ts.Object, eventArgs);

            // ---- the helper type
            TypeDefinition helper = PatchSites.AddHelperType(module, HelperNamespace, HelperType);
            var hooksMissing = new FieldDefinition("hooksMissing", FieldAttributes.Private | FieldAttributes.Static, ts.Boolean);
            helper.Fields.Add(hooksMissing);
            MethodDefinition forward = PatchSites.Forwarder(helper, "Forward", ts.Boolean, hook, ts.Object, ts.Object, ts.Object);

            // internal static bool Same(object database, object current, object incoming)
            // {
            //     bool r = false;
            //     if (!hooksMissing) { try { r = Forward(database, current, incoming); } catch { hooksMissing = true; } }
            //     return r;
            // }
            var same = new MethodDefinition("Same", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Boolean);
            same.Parameters.Add(new ParameterDefinition("database", ParameterAttributes.None, ts.Object));
            same.Parameters.Add(new ParameterDefinition("current", ParameterAttributes.None, ts.Object));
            same.Parameters.Add(new ParameterDefinition("incoming", ParameterAttributes.None, ts.Object));
            var r = new VariableDefinition(ts.Boolean);
            same.Body.Variables.Add(r);
            same.Body.InitLocals = true;
            {
                ILProcessor il = same.Body.GetILProcessor();
                Instruction result = il.Create(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldsfld, hooksMissing);
                il.Emit(OpCodes.Brtrue_S, result);
                Instruction tryStart = il.Create(OpCodes.Ldarg_0);
                il.Append(tryStart);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, forward);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Leave_S, result);
                Instruction handler = il.Create(OpCodes.Pop);
                il.Append(handler);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Stsfld, hooksMissing);
                il.Emit(OpCodes.Leave_S, result);
                il.Append(result);
                il.Emit(OpCodes.Ret);
                same.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = result, CatchType = ts.Object,
                });
            }
            helper.Methods.Add(same);

            // internal static bool Restore(object database, string path), Parsed(object, string), Forget(): the same latch.
            MethodDefinition restore = Latched(helper, hooksMissing, "Restore", ts.Boolean,
                PatchSites.Forwarder(helper, "ForwardRestore", ts.Boolean, restoreHook, ts.Object, ts.String), ts.Object, ts.String);
            MethodDefinition parsed = Latched(helper, hooksMissing, "Parsed", ts.Void,
                PatchSites.Forwarder(helper, "ForwardParsed", ts.Void, parsedHook, ts.Object, ts.String), ts.Object, ts.String);
            MethodDefinition forget = Latched(helper, hooksMissing, "Forget", ts.Void,
                PatchSites.Forwarder(helper, "ForwardForget", ts.Void, forgetHook));

            // ---- change: the same cells keep the tables
            {
                MethodBody body = setter.Body;
                ILProcessor il = body.GetILProcessor();
                Instruction first = body.Instructions[0];
                Instruction popRet = il.Create(OpCodes.Pop);
                var added = new[]
                {
                    il.Create(OpCodes.Ldarg_0),
                    il.Create(OpCodes.Ldarg_0),
                    il.Create(OpCodes.Ldfld, changer),
                    il.Create(OpCodes.Ldarg_1),
                    il.Create(OpCodes.Call, same),
                    il.Create(OpCodes.Brfalse, first),
                    il.Create(OpCodes.Ldarg_0),
                    il.Create(OpCodes.Ldarg_1),
                    il.Create(OpCodes.Stfld, changer),
                    il.Create(OpCodes.Ldarg_0),
                    il.Create(OpCodes.Ldfld, tablesLoaded),
                    il.Create(OpCodes.Dup),
                    il.Create(OpCodes.Brfalse, popRet),
                    il.Create(OpCodes.Ldarg_0),
                    il.Create(OpCodes.Ldsfld, emptyField),
                    il.Create(OpCodes.Callvirt, invoke),
                    il.Create(OpCodes.Ret),
                    popRet,
                    il.Create(OpCodes.Ret),
                };
                foreach (Instruction i in added) il.InsertBefore(first, i);
                PatchSites.FixShortBranches(setter);
            }
            context.Notes.Add(setter.FullName + ": a changer with the same CRM cells as the current one is stored and TablesLoaded raised, " +
                              "without Load(File) (the file was re-read and re-parsed for every CRM refresh before)");

            // ---- change: a file parsed before is restored from memory
            {
                MethodBody body = load.Body;
                ILProcessor il = body.GetILProcessor();
                Instruction first = body.Instructions[0];
                var prefix = new[]
                {
                    il.Create(OpCodes.Ldarg_0),
                    il.Create(OpCodes.Ldarg_1),
                    il.Create(OpCodes.Call, restore),
                    il.Create(OpCodes.Brfalse, first),
                    il.Create(OpCodes.Ldarg_0),
                    il.Create(OpCodes.Ldnull),
                    il.Create(OpCodes.Call, module.ImportReference(onTableLoaded)),
                    il.Create(OpCodes.Ret),
                };
                foreach (Instruction i in prefix) il.InsertBefore(first, i);
                InsertAtRegionEnd(load, tail, new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Call, parsed) });
            }
            {
                MethodBody body = dataChanged.Body;
                ILProcessor il = body.GetILProcessor();
                il.InsertBefore(body.Instructions[0], il.Create(OpCodes.Call, forget));
                PatchSites.FixShortBranches(dataChanged);
            }
            context.Notes.Add(load.FullName + ": a file whose freshly parsed rows are kept (from its second parse by a new database) is " +
                              "restored from memory, then OnTableLoaded(null) as after the parse (every reload, and every new per-user " +
                              "database of a bot, read and parsed the file before); " + dataChanged.Name + " forgets the kept rows");
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
            for (int p = 0; p < parameters.Length; p++)
            {
                Instruction load = PatchSites.LoadArgument(p);
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

        /// <summary>
        /// Inserts <paramref name="sequence"/> before <paramref name="at"/>, an instruction right after a protected region (here
        /// the end of Load's foreach finally and the target of its leave): every branch to <paramref name="at"/> and every region
        /// that ended there now ends at, and goes to, the first inserted instruction, so the sequence runs after the region.
        /// </summary>
        private static void InsertAtRegionEnd(MethodDefinition method, Instruction at, Instruction[] sequence)
        {
            MethodBody body = method.Body;
            if (body.ExceptionHandlers.Any(h => h.TryStart == at || h.HandlerStart == at || h.FilterStart == at))
                throw new PatchException(method.FullName + ": IL_" + at.Offset.ToString("x4") + " starts a protected region");
            var branches = PatchSites.BranchesTo(body, at);
            ILProcessor il = body.GetILProcessor();
            foreach (Instruction i in sequence) il.InsertBefore(at, i);
            Instruction first = sequence[0];
            foreach (Instruction branch in branches)
            {
                if (branch.Operand is Instruction[] targets)
                {
                    for (int k = 0; k < targets.Length; k++) if (targets[k] == at) targets[k] = first;
                }
                else
                {
                    branch.Operand = first;
                }
            }
            foreach (ExceptionHandler h in body.ExceptionHandlers)
            {
                if (h.TryEnd == at) h.TryEnd = first;
                if (h.HandlerEnd == at) h.HandlerEnd = first;
            }
            PatchSites.FixShortBranches(method);
        }
    }
}
