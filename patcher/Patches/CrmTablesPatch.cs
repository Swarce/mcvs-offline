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
    /// The setter is checked against the IL fingerprint of the verified build first.
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

        public override string Id => "crmtables";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "CRM tables: a database that gets the same CRM cells again keeps its tables instead of re-reading its file (Database.Changer; Gameloft.Engine.dll)";

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
            FieldDefinition changer = RequireField(database, "changer", ChangerType);
            FieldDefinition tablesLoaded = RequireField(database, "TablesLoaded", "System.EventHandler");
            if (changer.IsStatic || tablesLoaded.IsStatic) throw new PatchException(TypeName + ": changer/TablesLoaded are static");

            // ---- references
            TypeSystem ts = module.TypeSystem;
            MethodReference hook = ImportServerMethod(module, context, HooksType, "SameCrmChanges", "System.Boolean",
                                                      "System.Object", "System.Object", "System.Object");
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
        }
    }
}
