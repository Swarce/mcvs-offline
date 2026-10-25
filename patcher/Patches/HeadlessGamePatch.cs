using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Mandatory 'headless' patch of Game.dll (device test 1, RUN 2). The in-process TS and GS are headless Worlds
    /// (<c>ServerManager</c>: <c>new World(headless: true, createSceneComponents: true, server: true)</c>) built from the same
    /// SceneComponents.tx as the client. A component is created in a headless World unless it is
    /// <c>[KeepInHeadlessMode(false)]</c>, and in this release build no lifecycle exception is caught: one throw in a
    /// server World is rethrown by <c>MoreAsync.FireAndForgetTask</c> and <c>GameApplication.UpdateWorlds</c> and ends the
    /// player's process. Sites (all verified against the 1.17.32 IL before anything changes):
    /// <list type="bullet">
    /// <item>H1 <c>InputSettingsComponent</c> gets <c>[KeepInHeadlessMode(false)]</c>: its <c>OnStart</c> dereferences
    /// <c>World.LocalStorage</c>, which is null in every headless World (<c>LocalStorageComponent</c> is dropped there) -
    /// the device-1 crash - and would otherwise reset the player's process-wide InputManager sensitivity/invert settings.</item>
    /// <item>H2 <c>GameplayComponent</c>: the four <c>OfType&lt;HudGamepadComponent&gt;().FirstOrDefault().BindingTo*Preset()</c>
    /// calls (OnStart on every GS map load, OnStop at teardown, the two pause handlers) go through null-safe helpers;
    /// HudGamepadComponent is dropped in headless Worlds.</item>
    /// <item>H7 <c>FlameZoneComponent.BroadcastEnd</c> and <c>BacktrackTimeDistortionExplosion.FadeOutEffect</c>:
    /// <c>GetComponent&lt;VfxAnimatorComponent&gt;().TriggerEnd()</c> through a null-safe helper (VfxAnimator is dropped
    /// in headless Worlds; BroadcastEnd would otherwise throw, and send its RPC, every frame).</item>
    /// <item>H5 <c>SoundManagerGameComponent</c>: OnStart/OnUpdate return right after their base call and OnStop runs only
    /// its base call when <c>World.Headless</c>, so a server World never touches the player's VoxManager (OnStop stops
    /// every sound group but six).</item>
    /// </list>
    /// Client Worlds are unchanged: the attribute filter is <c>headless &amp;&amp; !KeepInHeadless</c>, the helpers call the
    /// original method whenever the component exists, and the early returns test <c>World.Headless</c>.
    /// </summary>
    public sealed class HeadlessGamePatch : IlPatch
    {
        public const string KeepAttribute = "Gameloft.Engine.KeepInHeadlessModeAttribute";

        public override string Id => "headless";

        public override string AssemblyFile => "Game.dll";

        public override string Description => "headless TS/GS Worlds: client-only components dropped or null-safe (Game.dll)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify every site first
            MethodReference keepCtor = module.Types.SelectMany(t => t.CustomAttributes)
                .Where(a => a.AttributeType.FullName == KeepAttribute).Select(a => a.Constructor).FirstOrDefault();
            if (keepCtor == null || keepCtor.Parameters.Count != 1 || keepCtor.Parameters[0].ParameterType.MetadataType != MetadataType.Boolean)
                throw new PatchException("Game.dll does not use " + KeepAttribute + "(bool)");

            TypeDefinition inputSettings = RequireType(module, "Game.InputSettingsComponent");
            RequireNoKeepAttribute(inputSettings);
            MethodDefinition inputStart = RequireMethod(inputSettings, "OnStart", m => !m.IsStatic && !m.HasParameters, "OnStart()");
            var os = inputStart.Body.Instructions;
            bool inputShape = os.Count > 7 && os[0].OpCode == OpCodes.Ldarg_0 && IsCall(os[1], "Gameloft.Engine.Component", "OnStart") &&
                              os[2].OpCode == OpCodes.Ldarg_0 && IsCall(os[3], "Gameloft.Engine.Component", "get_World") &&
                              IsCall(os[4], "Gameloft.Engine.World", "get_LocalStorage") &&
                              os[5].OpCode == OpCodes.Ldstr && (string)os[5].Operand == "Action1" &&
                              IsCall(os[6], "Gameloft.Engine.LocalStorageComponent", "HasMember");
            if (!inputShape)
            {
                throw new PatchException(inputStart.FullName + " does not start like the verified 1.17.32 code (base.OnStart(); " +
                                         "World.LocalStorage.HasMember(\"Action1\")):\n" + Dump(inputStart));
            }

            TypeDefinition gameplay = RequireType(module, "Game.GameplayComponent");
            TypeDefinition hud = RequireType(module, "Game.HudGamepadComponent");
            var hudTargets = new Dictionary<string, MethodDefinition>();
            foreach (string name in new[] { "BindingToActionPreset", "BindingToDefaultPreset" })
                hudTargets[name] = RequireMethod(hud, name, m => !m.IsStatic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.Void, "void " + name + "()");
            if (gameplay.Methods.Any(m => m.Name.StartsWith("Mcvs", System.StringComparison.Ordinal)))
                throw new PatchException("Game.dll is already patched: run the patcher on your original APK");
            var hudSites = new List<KeyValuePair<MethodDefinition, Instruction>>();
            foreach (MethodDefinition m in gameplay.Methods.Where(m => m.HasBody))
            {
                var code = m.Body.Instructions;
                for (int i = 0; i < code.Count; i++)
                {
                    if (!(code[i].Operand is MethodReference r) || r.DeclaringType.FullName != hud.FullName || !hudTargets.ContainsKey(r.Name)) continue;
                    bool afterFirstOrDefault = i > 0 && code[i].OpCode == OpCodes.Callvirt && code[i - 1].OpCode == OpCodes.Call &&
                                               code[i - 1].Operand is GenericInstanceMethod g && g.Name == "FirstOrDefault" &&
                                               g.DeclaringType.FullName == "System.Linq.Enumerable" && g.GenericArguments[0].FullName == hud.FullName;
                    if (!afterFirstOrDefault)
                        throw new PatchException(m.FullName + ": HudGamepadComponent::" + r.Name + " is not called on FirstOrDefault<HudGamepadComponent>():\n" + Dump(m));
                    hudSites.Add(new KeyValuePair<MethodDefinition, Instruction>(m, code[i]));
                }
            }
            var expected = new[] { "OnStart", "OnStop", "Ui_OnPauseGameplay", "Ui_OnUnPauseGameplay" };
            if (hudSites.Count != 4 || !hudSites.Select(s => s.Key.Name).OrderBy(n => n, System.StringComparer.Ordinal).SequenceEqual(expected))
            {
                throw new PatchException(gameplay.FullName + ": expected the 4 HudGamepadComponent calls in " + string.Join(", ", expected) + ", found " +
                                         string.Join(", ", hudSites.Select(s => s.Key.Name)));
            }

            TypeDefinition vfx = RequireType(module, "Game.VfxAnimatorComponent");
            MethodDefinition triggerEnd = RequireMethod(vfx, "TriggerEnd", m => !m.IsStatic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.Void, "void TriggerEnd()");
            var vfxSites = new List<KeyValuePair<MethodDefinition, Instruction>>();
            foreach (var site in new[] { new[] { "Game.FlameZoneComponent", "BroadcastEnd" }, new[] { "Game.BacktrackTimeDistortionExplosion", "FadeOutEffect" } })
            {
                TypeDefinition owner = RequireType(module, site[0]);
                MethodDefinition m = RequireMethod(owner, site[1], x => !x.IsStatic && !x.HasParameters, site[1] + "()");
                var code = m.Body.Instructions;
                var hits = Enumerable.Range(1, code.Count - 1)
                    .Where(i => code[i].OpCode == OpCodes.Callvirt && code[i].Operand is MethodReference r && r.Name == "TriggerEnd" &&
                                r.DeclaringType.FullName == vfx.FullName && code[i - 1].Operand is GenericInstanceMethod g && g.Name == "GetComponent" &&
                                g.DeclaringType.FullName == "Gameloft.Engine.Component" && g.GenericArguments[0].FullName == vfx.FullName)
                    .ToList();
                int triggerCalls = code.Count(i => i.Operand is MethodReference r && r.Name == "TriggerEnd");
                if (hits.Count != 1 || triggerCalls != 1)
                    throw new PatchException(m.FullName + ": expected one GetComponent<VfxAnimatorComponent>().TriggerEnd(), found " + hits.Count + ":\n" + Dump(m));
                vfxSites.Add(new KeyValuePair<MethodDefinition, Instruction>(m, code[hits[0]]));
            }

            TypeDefinition sound = RequireType(module, "Game.SoundManagerGameComponent");
            MethodDefinition soundStart = RequireMethod(sound, "OnStart", m => !m.IsStatic && !m.HasParameters, "OnStart()");
            MethodDefinition soundUpdate = RequireMethod(sound, "OnUpdate", m => !m.IsStatic && !m.HasParameters, "OnUpdate()");
            MethodDefinition soundStop = RequireMethod(sound, "OnStop", m => !m.IsStatic && !m.HasParameters, "OnStop()");
            RequireBaseCallFirst(soundStart, "Gameloft.Engine.SoundManagerComponent");
            RequireBaseCallFirst(soundUpdate, "Gameloft.Engine.Component");
            var baseStops = soundStop.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "OnStop" &&
                            r.DeclaringType.FullName == "Gameloft.Engine.SoundManagerComponent" && !r.HasParameters)
                .Select(i => (MethodReference)i.Operand).ToList();
            if (baseStops.Count != 1) throw new PatchException(soundStop.FullName + ": expected one base.OnStop() call:\n" + Dump(soundStop));

            MethodReference getWorld = FindCall(inputStart, "Gameloft.Engine.Component", "get_World");
            MethodReference getHeadless = ImportEngineMethod(module, "Gameloft.Engine.World", "get_Headless", MetadataType.Boolean);

            // ---- change
            AddKeepAttribute(inputSettings, keepCtor, false);
            context.Notes.Add(inputSettings.FullName + ": + [KeepInHeadlessMode(false)] (OnStart IL_0011 dereferences World.LocalStorage, null in headless Worlds)");

            var helpers = new Dictionary<string, MethodDefinition>();
            foreach (var target in hudTargets)
            {
                MethodDefinition helper = NullSafeHelper(module, "Mcvs" + target.Key, hud, target.Value);
                gameplay.Methods.Add(helper);
                helpers[target.Key] = helper;
            }
            var notes = new List<string>();
            foreach (var site in hudSites)
            {
                string name = ((MethodReference)site.Value.Operand).Name;
                notes.Add(site.Key.Name + " IL_" + site.Value.Offset.ToString("x4", CultureInfo.InvariantCulture) + " -> Mcvs" + name);
                site.Value.OpCode = OpCodes.Call;
                site.Value.Operand = helpers[name];
            }
            context.Notes.Add(gameplay.FullName + ": HudGamepadComponent calls null-safe (" + string.Join(", ", notes) + ")");

            foreach (var site in vfxSites)
            {
                MethodDefinition helper = NullSafeHelper(module, "McvsTriggerEnd", vfx, triggerEnd);
                site.Key.DeclaringType.Methods.Add(helper);
                context.Notes.Add(site.Key.FullName + ": IL_" + site.Value.Offset.ToString("x4", CultureInfo.InvariantCulture) +
                                  " callvirt VfxAnimatorComponent::TriggerEnd -> McvsTriggerEnd (null-safe)");
                site.Value.OpCode = OpCodes.Call;
                site.Value.Operand = helper;
            }

            ReturnAfterBaseCallWhenHeadless(soundStart, getWorld, getHeadless);
            ReturnAfterBaseCallWhenHeadless(soundUpdate, getWorld, getHeadless);
            OnlyBaseCallWhenHeadless(soundStop, baseStops[0], getWorld, getHeadless);
            context.Notes.Add(sound.FullName + ": OnStart/OnUpdate return after the base call and OnStop only calls base.OnStop() when World.Headless " +
                              "(server Worlds stay off the player's VoxManager)");
        }

        // ------------------------------------------------------------------ helpers shared with HeadlessEnginePatch

        internal static void RequireNoKeepAttribute(TypeDefinition type)
        {
            if (type.CustomAttributes.Any(a => a.AttributeType.FullName == KeepAttribute))
                throw new PatchException(type.FullName + " already has [KeepInHeadlessMode]: not the verified 1.17.32 code (or already patched)");
        }

        internal static void AddKeepAttribute(TypeDefinition type, MethodReference keepCtor, bool keep)
        {
            var attribute = new CustomAttribute(keepCtor);
            attribute.ConstructorArguments.Add(new CustomAttributeArgument(type.Module.TypeSystem.Boolean, keep));
            type.CustomAttributes.Add(attribute);
        }

        /// <summary>"ldarg.0; call &lt;baseType&gt;::&lt;same name&gt;()" as the first two instructions.</summary>
        internal static void RequireBaseCallFirst(MethodDefinition method, string baseType)
        {
            var code = method.Body.Instructions;
            if (!(code.Count > 2 && code[0].OpCode == OpCodes.Ldarg_0 && code[1].OpCode == OpCodes.Call && code[1].Operand is MethodReference r &&
                  r.Name == method.Name && r.DeclaringType.FullName == baseType && !r.HasParameters))
                throw new PatchException(method.FullName + " does not start with ldarg.0; call " + baseType + "::" + method.Name + "():\n" + Dump(method));
        }

        /// <summary>After the leading base call: <c>if (World.Headless) return;</c>.</summary>
        internal static void ReturnAfterBaseCallWhenHeadless(MethodDefinition method, MethodReference getWorld, MethodReference getHeadless)
        {
            ILProcessor il = method.Body.GetILProcessor();
            Instruction next = method.Body.Instructions[2];
            foreach (Instruction i in new[]
            {
                il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, getWorld), il.Create(OpCodes.Callvirt, getHeadless),
                il.Create(OpCodes.Brfalse, next), il.Create(OpCodes.Ret),
            })
            {
                il.InsertBefore(next, i);
            }
        }

        /// <summary>Prepends <c>if (World.Headless) { base.X(); return; }</c>.</summary>
        internal static void OnlyBaseCallWhenHeadless(MethodDefinition method, MethodReference baseCall, MethodReference getWorld, MethodReference getHeadless)
        {
            ILProcessor il = method.Body.GetILProcessor();
            Instruction first = method.Body.Instructions[0];
            foreach (Instruction i in new[]
            {
                il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, getWorld), il.Create(OpCodes.Callvirt, getHeadless),
                il.Create(OpCodes.Brfalse, first), il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, baseCall), il.Create(OpCodes.Ret),
            })
            {
                il.InsertBefore(first, i);
            }
        }

        /// <summary><c>private static void name(T x) { if (x != null) x.target(); }</c></summary>
        private static MethodDefinition NullSafeHelper(ModuleDefinition module, string name, TypeReference parameterType, MethodReference target)
        {
            var helper = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
            helper.Parameters.Add(new ParameterDefinition("component", ParameterAttributes.None, parameterType));
            ILProcessor il = helper.Body.GetILProcessor();
            Instruction ret = il.Create(OpCodes.Ret);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Brfalse, ret);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, target);
            il.Append(ret);
            return helper;
        }

        private static MethodReference FindCall(MethodDefinition method, string declaringType, string name)
        {
            return method.Body.Instructions.Where(i => IsCall(i, declaringType, name)).Select(i => (MethodReference)i.Operand).First();
        }

        /// <summary>An instance getter of Gameloft.Engine.dll, imported into <paramref name="module"/>.</summary>
        private static MethodReference ImportEngineMethod(ModuleDefinition module, string typeName, string name, MetadataType returnType)
        {
            AssemblyNameReference engine = module.AssemblyReferences.SingleOrDefault(a => a.Name == "Gameloft.Engine");
            if (engine == null) throw new PatchException(module.Name + " does not reference Gameloft.Engine");
            TypeDefinition type = module.AssemblyResolver.Resolve(engine)?.MainModule.GetType(typeName);
            MethodDefinition method = type?.Methods.SingleOrDefault(m => m.Name == name && !m.IsStatic && !m.HasParameters && m.ReturnType.MetadataType == returnType);
            if (method == null) throw new PatchException("Gameloft.Engine.dll has no " + typeName + "::" + name);
            return module.ImportReference(method);
        }
    }
}
