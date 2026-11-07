using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// 'botmove' patch of Game.dll (build 27; default on PC, optional on Android): a bot standing still always takes its first
    /// step, whatever the frame rate.
    /// <para>
    /// <c>BT_Action_MoveTo.adaptSpeed</c> ramps a bot's speed up from its current input: <c>speed = Sqrt(Forward² + Strafe²) +
    /// world.DeltaTime.Seconds * acceleration</c> (<c>acceleration</c> = 3), and <c>moveToVector</c> stops the bot again unless
    /// <c>speed &gt; 0.05</c>. From rest the first step is exactly 3*dt, so a resting bot starts only on an AI-tick frame of at
    /// least 1/60 s (float32: 3 * 1/60 = 0.05000001). Gameloft's dedicated servers ran at 40 frames/s (dt 25 ms); in the client,
    /// whose frame dt every World gets, the PC's 60 FPS limit (WCPToolkit's pacer ends each frame about 0.1 ms early: dt about
    /// 16.57 ms, 3*dt = 0.0497) left every bot at rest where it stood, until a short path entered the deceleration branch
    /// (docs/research/07-offline-bots.md §15). After the first step the ramp is frame-rate independent.
    /// </para>
    /// <para>
    /// The change, right after the ramp's <c>stloc.0</c> and before the <c>&gt; 1</c> clamp (the decel branch and
    /// <c>forceDefaultSpeed</c> are untouched):
    /// <code>
    /// num = GameMath.Sqrt(num4) + world.DeltaTime.Seconds * acceleration;
    /// num = McvsBotMove.Ramp(num, num4, world.DeltaTime.Seconds);   // added (local 10 still holds DeltaTime)
    /// if (num &gt; 1f) num = 1f;
    /// </code>
    /// <c>Gameloft.Sequences.McvsBotMove.Ramp</c> asks <c>McvsOffline.GameHooks.BotMoveRamp</c> (settings
    /// <c>embedded.bots.moveFromRest</c>, the "bots:" log lines) through a [NoInlining] forwarder; without McvsOffline.Server.dll
    /// the IL itself raises a step of at most 0.05 to 0.051 (what a 17 ms frame gives), and a <c>hooksMissing</c> latch makes
    /// that cost one exception, not one per call. A step above 0.05 (every frame of at least 1/60 s) is returned bit for bit.
    /// </para>
    /// The IL of <c>adaptSpeed</c> is identical on Android 1.17.32 and Steam 6372049: one fingerprint for both builds.
    /// </summary>
    public sealed class BotMovePatch : IlPatch
    {
        public const string TypeName = "Gameloft.Sequences.BT_Action_MoveTo";
        public const string HelperNamespace = "Gameloft.Sequences";
        public const string HelperType = "McvsBotMove";
        public const string AdaptSpeedFingerprint = "88f1ea324aba6c96beba0b604173d751142735bbcb19267d9951f430ac9c4a78";

        /// <summary><c>moveToVector</c>'s start threshold: a speed must be above it or the bot is stopped.</summary>
        public const float StartThreshold = 0.05f;

        /// <summary>The first step the IL alone gives a resting bot (no McvsOffline.Server.dll): a 17 ms frame's 3*dt.</summary>
        public const float FirstStep = 0.051f;

        public override string Id => "botmove";

        public override string AssemblyFile => "Game.dll";

        public override string Description => "bots: a resting bot always takes its first step, at any frame rate (BT_Action_MoveTo.adaptSpeed; Game.dll)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify
            PatchSites.RequireBuildAssembly(module, context, Id);
            string fingerprint = ForBuild(context, AdaptSpeedFingerprint, AdaptSpeedFingerprint);
            TypeDefinition moveTo = RequireType(module, TypeName);
            if (module.GetType(HelperNamespace + "." + HelperType) != null)
                throw new PatchException("Game.dll is already patched: run the patcher on your original game files");
            MethodDefinition adaptSpeed = RequireMethod(moveTo, "adaptSpeed",
                m => !m.IsStatic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.Single, "float32 adaptSpeed()");
            RequireFingerprint(adaptSpeed, fingerprint, "BT_Action_MoveTo.adaptSpeed()");
            FieldDefinition acceleration = RequireField(moveTo, "acceleration", "System.Single");
            if (!acceleration.IsStatic) throw new PatchException(TypeName + "::acceleration is not static");

            // The site, as fingerprinted (IL_0234..IL_0241):
            //   ldloca.s 10; call Time::get_Seconds(); ldsfld acceleration; mul; add; stloc.0 | ldloc.0; ldc.r4 1; ble.un.s
            MethodBody body = adaptSpeed.Body;
            var code = body.Instructions;
            int[] reads = Enumerable.Range(0, code.Count).Where(i => code[i].OpCode == OpCodes.Ldsfld && code[i].Operand is FieldReference f &&
                                                                     f.Name == "acceleration" && f.DeclaringType.FullName == TypeName).ToArray();
            if (reads.Length != 1) throw new PatchException(adaptSpeed.FullName + ": expected one read of acceleration, found " + reads.Length);
            int site = reads[0];
            if (!(site >= 2 && site + 5 < code.Count &&
                  code[site - 2].OpCode == OpCodes.Ldloca_S && code[site - 1].OpCode == OpCodes.Call && IsCall(code[site - 1], "Gameloft.Engine.Time", "get_Seconds") &&
                  code[site + 1].OpCode == OpCodes.Mul && code[site + 2].OpCode == OpCodes.Add && code[site + 3].OpCode == OpCodes.Stloc_0 &&
                  code[site + 4].OpCode == OpCodes.Ldloc_0 && code[site + 5].OpCode == OpCodes.Ldc_R4 && (float)code[site + 5].Operand == 1f))
                throw new PatchException(adaptSpeed.FullName + ": the ramp site (ldsfld acceleration; mul; add; stloc.0) is not where the fingerprint says:\n" + Dump(adaptSpeed));
            Instruction store = code[site + 3];
            Instruction after = code[site + 4];
            if (body.Instructions.Any(i => i.Operand == after || (i.Operand is Instruction[] targets && targets.Contains(after))) || body.HasExceptionHandlers)
                throw new PatchException(adaptSpeed.FullName + ": unexpected branch or handler at the ramp site");
            if (body.Variables.Count != 12 || body.Variables[10].VariableType.FullName != "Gameloft.Engine.Time" ||
                body.Variables[11].VariableType.MetadataType != MetadataType.Single || body.Variables[0].VariableType.MetadataType != MetadataType.Single ||
                code[site - 2].Operand != body.Variables[10])
                throw new PatchException(adaptSpeed.FullName + ": locals 0, 10 and 11 are not float32, Time and float32");
            var getSeconds = (MethodReference)code[site - 1].Operand;

            TypeSystem ts = module.TypeSystem;
            MethodReference hook = ImportServerMethod(module, context, NativeOnlinePatch.HooksType, "BotMoveRamp", "System.Single",
                                                      "System.Single", "System.Single", "System.Single");

            // ---- the helper type
            TypeDefinition helper = PatchSites.AddHelperType(module, HelperNamespace, HelperType);
            // private static bool hooksMissing;   (set once a call to McvsOffline.Server.dll failed: the IL decides alone from then on)
            var hooksMissing = new FieldDefinition("hooksMissing", FieldAttributes.Private | FieldAttributes.Static, ts.Boolean);
            helper.Fields.Add(hooksMissing);
            // [NoInlining] private static float Forward(float a0, float a1, float a2) => GameHooks.BotMoveRamp(a0, a1, a2);
            MethodDefinition forward = PatchSites.Forwarder(helper, "Forward", ts.Single, hook, ts.Single, ts.Single, ts.Single);

            // internal static float Ramp(float ramped, float prevSq, float dt)
            // {
            //     float r = ramped <= 0.05f ? 0.051f : ramped;                    // without McvsOffline.Server.dll
            //     if (!hooksMissing) { try { r = Forward(ramped, prevSq, dt); } catch { hooksMissing = true; } }
            //     return r;
            // }
            var ramp = new MethodDefinition("Ramp", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Single);
            ramp.Parameters.Add(new ParameterDefinition("ramped", ParameterAttributes.None, ts.Single));
            ramp.Parameters.Add(new ParameterDefinition("prevSq", ParameterAttributes.None, ts.Single));
            ramp.Parameters.Add(new ParameterDefinition("dt", ParameterAttributes.None, ts.Single));
            var r = new VariableDefinition(ts.Single);
            ramp.Body.Variables.Add(r);
            ramp.Body.InitLocals = true;
            {
                ILProcessor il = ramp.Body.GetILProcessor();
                Instruction keep = il.Create(OpCodes.Ldarg_0);
                Instruction set = il.Create(OpCodes.Stloc_0);
                Instruction result = il.Create(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_R4, StartThreshold);
                il.Emit(OpCodes.Bgt_Un_S, keep);           // above the threshold (or NaN): keep it
                il.Emit(OpCodes.Ldc_R4, FirstStep);
                il.Emit(OpCodes.Br_S, set);
                il.Append(keep);
                il.Append(set);
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
                ramp.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = result, CatchType = ts.Object,
                });
            }
            helper.Methods.Add(ramp);

            // ---- change: the ramp's step goes through McvsBotMove.Ramp(num, num4, DeltaTime.Seconds) before the clamp
            {
                ILProcessor il = body.GetILProcessor();
                il.InsertAfter(store, il.Create(OpCodes.Stloc_0));
                il.InsertAfter(store, il.Create(OpCodes.Call, ramp));
                il.InsertAfter(store, il.Create(OpCodes.Call, getSeconds));
                il.InsertAfter(store, il.Create(OpCodes.Ldloca_S, body.Variables[10]));
                il.InsertAfter(store, il.Create(OpCodes.Ldloc_S, body.Variables[11]));
                il.InsertAfter(store, il.Create(OpCodes.Ldloc_0));
                PatchSites.FixShortBranches(adaptSpeed);
            }
            context.Notes.Add(adaptSpeed.FullName + ": the ramp's step from rest goes through " + HelperNamespace + "." + HelperType +
                              ".Ramp(speed, input², DeltaTime) (a resting bot starts at any frame rate; unchanged when the frame lasts at least 1/60 s; " +
                              "settings embedded.bots.moveFromRest)");
        }
    }
}
