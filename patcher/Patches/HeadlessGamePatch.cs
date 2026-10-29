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
            PatchSites.RequireBuildCorlib(module, context, Id);
            if (context.Build.IsPc) ApplyPc(module, context);
            else ApplyAndroid(module, context);
        }

        // ------------------------------------------------------------------ Android 1.17.32: H1, H2, H5, H7

        private void ApplyAndroid(ModuleDefinition module, PatchContext context)
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

        // ------------------------------------------------------------------ PC (Steam 6372049): docs/design/pc-port.md §8.4

        /// <summary>H1 <c>InputSettingsComponent.OnStart()</c> on PC: <c>base.OnStart(); World.GetUI().OverrideNativeFunction("ChangeKeyAction", …)</c>.</summary>
        public const string PcInputSettingsStartFingerprint = "267c5ba0f22da50d67f0566be5418fea526a287ea5e3ebef25fa79529b6197b2";

        /// <summary>H5 <c>SoundManagerGameComponent.OnStart/OnUpdate/OnStop</c> (identical on Android).</summary>
        public const string PcSoundStartFingerprint = "7df996c253f71dbf38e3f957d140db3e4fb3971d970bc26574e2b3c8c79d858c";
        public const string PcSoundUpdateFingerprint = "ef8927e6b9849caac7859b57db4f00ac152824f05ca88f4678a644b7cabf21fc";
        public const string PcSoundStopFingerprint = "11f42f040c870045dea9104161afa39c0bd6999ab850e6671173a654262e20ac";

        /// <summary>H7 <c>FlameZoneComponent.BroadcastEnd</c> (the source path literal differs from Android) and
        /// <c>BacktrackTimeDistortionExplosion.FadeOutEffect</c> (identical).</summary>
        public const string PcFlameZoneEndFingerprint = "1bbd54ea044f6ffc7cca33cf2d3cbd6d212fabacc1d460a5cb8bfed4ecb59d99";
        public const string PcBacktrackFadeFingerprint = "278ecc0855660fe3285d48224516e06f624d7fbaf14c8754dafc14dece12980e";

        /// <summary>H8 <c>GamePreferencesComponent.LoadPreferencesWindows()</c>.</summary>
        public const string PcLoadPreferencesFingerprint = "5740df958d71ef2f7df4182ff61a491f64d24f2e9414a2687269212f2989d360";

        /// <summary>H9 <c>SteamAchievementsComponent.OnStart()</c>.</summary>
        public const string PcSteamStartFingerprint = "58d802a47305cdce4b612c3ec5c9e0c5299c8957d8da932969197f3c4b32daae";

        /// <summary>M3 <c>SteamAchievementsComponent.OnStop()</c> (calls <c>base.OnStart()</c>: the stock bug).</summary>
        public const string PcSteamStopFingerprint = "dfddfc3d3cdbf0718fdd5b22420c1d09980c0933b3e0f16aa91861d7357c1b11";

        /// <summary>H10 <c>GameClientComponent.OnCreate()</c>.</summary>
        public const string PcClientCreateFingerprint = "fbcb1d22d85d1cbebd6fb653f0a733e0d036d539b5ca6d5334800ede32be0198";

        /// <summary>H11 <c>GameplayComponent.OnAppFocusLost()</c>.</summary>
        public const string PcFocusLostFingerprint = "7dc375ccfa644c88da2ab2b679c251ddb16a12b3ad0a7987460e18bfdeeb565f";

        /// <summary>M2 <c>GameplayComponent.OnUpdate()</c>.</summary>
        public const string PcGameplayUpdateFingerprint = "3076ed790ccb3026b3053aa350c587d32b990b2c45c51aaeb3d29786c1e10444";

        /// <summary>H13 <c>TutorialComponent.OnStart()</c>.</summary>
        public const string PcTutorialStartFingerprint = "7ff0b308b3b478cb06385c18afe49e8fb521bbab9e3525ca9f0dead5768fc499";

        /// <summary>
        /// The PC sites (§8.4): H1 (attribute, PC shape), H2 (no HudGamepad calls on PC), H5 and H7 (as on Android), and the
        /// PC-only guards H8, H9, M3, H10, H11, M2, H13. Every site is checked against its fingerprint and offsets before anything
        /// changes; every inserted <c>if (World.Headless)</c> is <c>ldarg.0; call Component::get_World; callvirt World::get_Headless;
        /// brtrue|brfalse</c>, and branches that targeted an insertion point are retargeted to the inserted guard.
        /// </summary>
        private void ApplyPc(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify every site first
            MethodReference keepCtor = module.Types.SelectMany(t => t.CustomAttributes)
                .Where(a => a.AttributeType.FullName == KeepAttribute).Select(a => a.Constructor).FirstOrDefault();
            if (keepCtor == null || keepCtor.Parameters.Count != 1 || keepCtor.Parameters[0].ParameterType.MetadataType != MetadataType.Boolean)
                throw new PatchException("Game.dll does not use " + KeepAttribute + "(bool)");

            // H1
            TypeDefinition inputSettings = RequireType(module, "Game.InputSettingsComponent");
            RequireNoKeepAttribute(inputSettings);
            MethodDefinition inputStart = RequireMethod(inputSettings, "OnStart", m => !m.IsStatic && !m.HasParameters, "OnStart()");
            RequireFingerprint(inputStart, PcInputSettingsStartFingerprint, "InputSettingsComponent.OnStart (H1)");
            var os = inputStart.Body.Instructions;
            if (!(os[0].OpCode == OpCodes.Ldarg_0 && IsCall(os[1], "Gameloft.Engine.Component", "OnStart") && os[2].OpCode == OpCodes.Ldarg_0 &&
                  IsCall(os[3], "Gameloft.Engine.Component", "get_World") && IsCall(os[4], "Game.UISceneExtentions", "GetUI") &&
                  os[5].OpCode == OpCodes.Ldstr && (string)os[5].Operand == "ChangeKeyAction"))
                throw new PatchException(inputStart.FullName + " does not start like the verified PC code (base.OnStart(); World.GetUI()...):\n" + Dump(inputStart));

            // H2: the PC GameplayComponent has no HudGamepadComponent calls to guard (key listeners instead).
            TypeDefinition gameplay = RequireType(module, "Game.GameplayComponent");
            int hudCalls = gameplay.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                .Count(i => i.Operand is GenericInstanceMethod g && g.Name == "FirstOrDefault" && g.GenericArguments.Count == 1 &&
                            g.GenericArguments[0].FullName == "Game.HudGamepadComponent");
            if (hudCalls != 0) throw new PatchException(gameplay.FullName + ": expected no FirstOrDefault<HudGamepadComponent>() call on PC, found " + hudCalls);

            // H5
            TypeDefinition sound = RequireType(module, "Game.SoundManagerGameComponent");
            MethodDefinition soundStart = RequireMethod(sound, "OnStart", m => !m.IsStatic && !m.HasParameters, "OnStart()");
            MethodDefinition soundUpdate = RequireMethod(sound, "OnUpdate", m => !m.IsStatic && !m.HasParameters, "OnUpdate()");
            MethodDefinition soundStop = RequireMethod(sound, "OnStop", m => !m.IsStatic && !m.HasParameters, "OnStop()");
            RequireFingerprint(soundStart, PcSoundStartFingerprint, "SoundManagerGameComponent.OnStart (H5)");
            RequireFingerprint(soundUpdate, PcSoundUpdateFingerprint, "SoundManagerGameComponent.OnUpdate (H5)");
            RequireFingerprint(soundStop, PcSoundStopFingerprint, "SoundManagerGameComponent.OnStop (H5)");
            RequireBaseCallFirst(soundStart, "Gameloft.Engine.SoundManagerComponent");
            RequireBaseCallFirst(soundUpdate, "Gameloft.Engine.Component");
            var baseStops = soundStop.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "OnStop" &&
                            r.DeclaringType.FullName == "Gameloft.Engine.SoundManagerComponent" && !r.HasParameters)
                .Select(i => (MethodReference)i.Operand).ToList();
            if (baseStops.Count != 1) throw new PatchException(soundStop.FullName + ": expected one base.OnStop() call:\n" + Dump(soundStop));

            // H7
            TypeDefinition vfx = RequireType(module, "Game.VfxAnimatorComponent");
            MethodDefinition triggerEnd = RequireMethod(vfx, "TriggerEnd", m => !m.IsStatic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.Void, "void TriggerEnd()");
            var vfxSites = new List<KeyValuePair<MethodDefinition, Instruction>>();
            foreach (var site in new[]
            {
                new[] { "Game.FlameZoneComponent", "BroadcastEnd", PcFlameZoneEndFingerprint },
                new[] { "Game.BacktrackTimeDistortionExplosion", "FadeOutEffect", PcBacktrackFadeFingerprint },
            })
            {
                TypeDefinition owner = RequireType(module, site[0]);
                MethodDefinition m = RequireMethod(owner, site[1], x => !x.IsStatic && !x.HasParameters, site[1] + "()");
                RequireFingerprint(m, site[2], site[0] + "." + site[1] + " (H7)");
                vfxSites.Add(new KeyValuePair<MethodDefinition, Instruction>(m, SingleTriggerEnd(m, vfx)));
            }

            // H8: before IL_0025 (ldarg.0; stsfld instance), not a branch target: if (World.Headless) goto IL_002b
            TypeDefinition preferences = RequireType(module, "Game.GamePreferencesComponent");
            MethodDefinition loadWindows = RequireMethod(preferences, "LoadPreferencesWindows", m => !m.IsStatic && !m.HasParameters, "LoadPreferencesWindows()");
            RequireFingerprint(loadWindows, PcLoadPreferencesFingerprint, "GamePreferencesComponent.LoadPreferencesWindows (H8)");
            Instruction h8At = PatchSites.At(loadWindows, 0x25), h8Target = PatchSites.At(loadWindows, 0x2b);
            if (!(h8At.OpCode == OpCodes.Ldarg_0 && h8At.Next.OpCode == OpCodes.Stsfld && ((FieldReference)h8At.Next.Operand).Name == "instance" &&
                  h8At.Next.Next == h8Target))
                throw new PatchException(loadWindows.FullName + ": IL_0025 is not 'ldarg.0; stsfld instance':\n" + Dump(loadWindows));
            RequireBranchSources(loadWindows, h8At);

            // H9: before IL_0038 (block B; targeted by IL_0011 and IL_0019): if (World.Headless) goto IL_00c5 (ret)
            TypeDefinition steam = RequireType(module, "Game.SteamAchievementsComponent");
            MethodDefinition steamStart = RequireMethod(steam, "OnStart", m => !m.IsStatic && !m.HasParameters, "OnStart()");
            RequireFingerprint(steamStart, PcSteamStartFingerprint, "SteamAchievementsComponent.OnStart (H9)");
            Instruction h9At = PatchSites.At(steamStart, 0x38), h9Target = PatchSites.At(steamStart, 0xc5);
            if (h9Target.OpCode != OpCodes.Ret || h9At.OpCode != OpCodes.Ldarg_0)
                throw new PatchException(steamStart.FullName + ": IL_0038/IL_00c5 are not where the fingerprint says:\n" + Dump(steamStart));
            RequireBranchSources(steamStart, h9At, PatchSites.At(steamStart, 0x11), PatchSites.At(steamStart, 0x19));

            // M3: headless (server) Worlds call the base OnStop instead of the stock base.OnStart()
            MethodDefinition steamStop = RequireMethod(steam, "OnStop", m => !m.IsStatic && !m.HasParameters, "OnStop()");
            RequireFingerprint(steamStop, PcSteamStopFingerprint, "SteamAchievementsComponent.OnStop (M3)");
            var stopCode = steamStop.Body.Instructions;
            if (!(stopCode[0].OpCode == OpCodes.Ldarg_0 && stopCode[1].OpCode == OpCodes.Call && IsCall(stopCode[1], "Game.BaseAchievementsComponent", "OnStart") &&
                  stopCode.Count(i => i.Operand is MethodReference r && r.Name == "OnStart") == 1 && stopCode[2].Offset == 0x6))
                throw new PatchException(steamStop.FullName + ": does not start with ldarg.0; call BaseAchievementsComponent::OnStart():\n" + Dump(steamStop));
            RequireBranchSources(steamStop, stopCode[0]);
            MethodReference baseStop = module.ImportReference(NearestBaseMethod(steam, "OnStop"));

            // H10: before IL_0079 (the PC-only tail; not a branch target): if (World.Headless) goto IL_0107 (ret)
            TypeDefinition gameClient = RequireType(module, "Game.GameClientComponent");
            MethodDefinition clientCreate = RequireMethod(gameClient, "OnCreate", m => !m.IsStatic && !m.HasParameters, "OnCreate()");
            RequireFingerprint(clientCreate, PcClientCreateFingerprint, "GameClientComponent.OnCreate (H10)");
            Instruction h10At = PatchSites.At(clientCreate, 0x79), h10Target = PatchSites.At(clientCreate, 0x107);
            if (!(h10At.OpCode == OpCodes.Ldarg_0 && IsCall(h10At.Next, "Gameloft.Engine.Component", "get_World") &&
                  IsCall(h10At.Next.Next, "Game.GameplaySceneExtentions", "GetGameInputManager") &&
                  h10At.Next.Next.Next.OpCode == OpCodes.Ldstr && (string)h10At.Next.Next.Next.Operand == "Config\\Presets.txt" && h10Target.OpCode == OpCodes.Ret))
                throw new PatchException(clientCreate.FullName + ": IL_0079 is not the GetGameInputManager().Init(\"Config\\Presets.txt\") tail:\n" + Dump(clientCreate));
            RequireBranchSources(clientCreate, h10At);

            // H11: prologue (nothing branches to IL_0000): if (World.Headless) goto IL_001c (ret)
            MethodDefinition focusLost = RequireMethod(gameplay, "OnAppFocusLost", m => !m.IsStatic && !m.HasParameters, "OnAppFocusLost()");
            RequireFingerprint(focusLost, PcFocusLostFingerprint, "GameplayComponent.OnAppFocusLost (H11)");
            Instruction h11At = focusLost.Body.Instructions[0], h11Target = PatchSites.At(focusLost, 0x1c);
            if (h11Target.OpCode != OpCodes.Ret) throw new PatchException(focusLost.FullName + ": IL_001c is not the ret:\n" + Dump(focusLost));
            RequireBranchSources(focusLost, h11At);

            // M2: before IL_0057 (the cursor block; targeted by IL_0025 and IL_0044): if (World.Headless) goto IL_0109
            MethodDefinition gameplayUpdate = RequireMethod(gameplay, "OnUpdate", m => !m.IsStatic && !m.HasParameters, "OnUpdate()");
            RequireFingerprint(gameplayUpdate, PcGameplayUpdateFingerprint, "GameplayComponent.OnUpdate (M2)");
            Instruction m2At = PatchSites.At(gameplayUpdate, 0x57), m2Target = PatchSites.At(gameplayUpdate, 0x109);
            if (!(new[] { 0x97, 0xfc, 0x104 }.All(o => IsCall(PatchSites.At(gameplayUpdate, o), "Gameloft.Engine.InputManager", "ShowCursor")) &&
                  m2Target.OpCode == OpCodes.Ldarg_0 && m2Target.Next.OpCode == OpCodes.Ldfld && ((FieldReference)m2Target.Next.Operand).Name == "m_isShowRoundResult"))
                throw new PatchException(gameplayUpdate.FullName + ": the cursor block IL_0057..IL_0109 is not where the fingerprint says:\n" + Dump(gameplayUpdate));
            RequireBranchSources(gameplayUpdate, m2At, PatchSites.At(gameplayUpdate, 0x25), PatchSites.At(gameplayUpdate, 0x44));

            // H13: after IL_001f (call GetUI): dup; brtrue IL_0024; pop; br IL_0033
            TypeDefinition tutorial = RequireType(module, "Game.TutorialComponent");
            MethodDefinition tutorialStart = RequireMethod(tutorial, "OnStart", m => !m.IsStatic && !m.HasParameters, "OnStart()");
            RequireFingerprint(tutorialStart, PcTutorialStartFingerprint, "TutorialComponent.OnStart (H13)");
            Instruction h13Ui = PatchSites.At(tutorialStart, 0x1f), h13Next = PatchSites.At(tutorialStart, 0x24), h13Skip = PatchSites.At(tutorialStart, 0x33);
            if (!(IsCall(h13Ui, "Game.UISceneExtentions", "GetUI") && h13Ui.Next == h13Next && h13Next.OpCode == OpCodes.Ldstr &&
                  IsCall(h13Skip.Previous, "Gameloft.Engine.MenuComponent", "DispatchEvent")))
                throw new PatchException(tutorialStart.FullName + ": IL_001f..IL_0033 is not the GetUI().DispatchEvent(...) block:\n" + Dump(tutorialStart));
            RequireBranchSources(tutorialStart, h13Next);

            MethodReference getWorld = FindCall(inputStart, "Gameloft.Engine.Component", "get_World");
            MethodReference getHeadless = ImportEngineMethod(module, "Gameloft.Engine.World", "get_Headless", MetadataType.Boolean);

            // ---- change
            AddKeepAttribute(inputSettings, keepCtor, false);
            context.Notes.Add(inputSettings.FullName + ": + [KeepInHeadlessMode(false)] (OnStart IL_000c World.GetUI() is null in headless Worlds) (H1)");

            ReturnAfterBaseCallWhenHeadless(soundStart, getWorld, getHeadless);
            ReturnAfterBaseCallWhenHeadless(soundUpdate, getWorld, getHeadless);
            OnlyBaseCallWhenHeadless(soundStop, baseStops[0], getWorld, getHeadless);
            context.Notes.Add(sound.FullName + ": OnStart/OnUpdate return after the base call and OnStop only calls base.OnStop() when World.Headless (H5)");

            foreach (var site in vfxSites)
            {
                MethodDefinition helper = NullSafeHelper(module, "McvsTriggerEnd", vfx, triggerEnd);
                site.Key.DeclaringType.Methods.Add(helper);
                context.Notes.Add(site.Key.FullName + ": IL_" + site.Value.Offset.ToString("x4", CultureInfo.InvariantCulture) +
                                  " callvirt VfxAnimatorComponent::TriggerEnd -> McvsTriggerEnd (null-safe) (H7)");
                site.Value.OpCode = OpCodes.Call;
                site.Value.Operand = helper;
            }

            PatchSites.InsertBeforeRetargeted(loadWindows, h8At, PatchSites.HeadlessBranch(getWorld, getHeadless, true, h8Target));
            context.Notes.Add(loadWindows.FullName + ": headless Worlds skip 'instance = this' (IL_0025) (H8)");

            PatchSites.InsertBeforeRetargeted(steamStart, h9At, PatchSites.HeadlessBranch(getWorld, getHeadless, true, h9Target));
            context.Notes.Add(steamStart.FullName + ": headless Worlds return before block B (IL_0038; IL_0011 and IL_0019 retargeted) (H9)");

            Instruction stockStart = stopCode[0], afterStockCall = stopCode[2];
            var m3 = new List<Instruction>(PatchSites.HeadlessBranch(getWorld, getHeadless, false, stockStart))
            {
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, baseStop), Instruction.Create(OpCodes.Br, afterStockCall),
            };
            PatchSites.InsertBeforeRetargeted(steamStop, stockStart, m3);
            context.Notes.Add(steamStop.FullName + ": headless Worlds call " + baseStop.DeclaringType.Name + ".OnStop() instead of the stock " +
                              "base.OnStart() (M3)");

            PatchSites.InsertBeforeRetargeted(clientCreate, h10At, PatchSites.HeadlessBranch(getWorld, getHeadless, true, h10Target));
            context.Notes.Add(clientCreate.FullName + ": headless Worlds return before the input/LocalStorage tail (IL_0079) (H10)");

            PatchSites.InsertBeforeRetargeted(focusLost, h11At, PatchSites.HeadlessBranch(getWorld, getHeadless, true, h11Target));
            context.Notes.Add(focusLost.FullName + ": headless Worlds return at once (H11)");

            PatchSites.InsertBeforeRetargeted(gameplayUpdate, m2At, PatchSites.HeadlessBranch(getWorld, getHeadless, true, m2Target));
            context.Notes.Add(gameplayUpdate.FullName + ": headless Worlds skip the cursor block (IL_0057..IL_0109; IL_0025 and IL_0044 retargeted) (M2)");

            ILProcessor tutorialIl = tutorialStart.Body.GetILProcessor();
            foreach (Instruction i in new[]
            {
                Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Brtrue, h13Next), Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Br, h13Skip),
            })
            {
                tutorialIl.InsertBefore(h13Next, i);
            }
            PatchSites.FixShortBranches(tutorialStart);
            context.Notes.Add(tutorialStart.FullName + ": World.GetUI() null-checked before DispatchEvent(\"HIDE_AT_START_HUD\") (H13)");
        }

        /// <summary>The single <c>GetComponent&lt;VfxAnimatorComponent&gt;().TriggerEnd()</c> call of <paramref name="m"/>.</summary>
        private static Instruction SingleTriggerEnd(MethodDefinition m, TypeDefinition vfx)
        {
            var code = m.Body.Instructions;
            var hits = Enumerable.Range(1, code.Count - 1)
                .Where(i => code[i].OpCode == OpCodes.Callvirt && code[i].Operand is MethodReference r && r.Name == "TriggerEnd" &&
                            r.DeclaringType.FullName == vfx.FullName && code[i - 1].Operand is GenericInstanceMethod g && g.Name == "GetComponent" &&
                            g.DeclaringType.FullName == "Gameloft.Engine.Component" && g.GenericArguments[0].FullName == vfx.FullName)
                .ToList();
            int triggerCalls = code.Count(i => i.Operand is MethodReference r && r.Name == "TriggerEnd");
            if (hits.Count != 1 || triggerCalls != 1)
                throw new PatchException(m.FullName + ": expected one GetComponent<VfxAnimatorComponent>().TriggerEnd(), found " + hits.Count + ":\n" + Dump(m));
            return code[hits[0]];
        }

        /// <summary>Throws unless exactly <paramref name="sources"/> branch to <paramref name="target"/>.</summary>
        private static void RequireBranchSources(MethodDefinition method, Instruction target, params Instruction[] sources)
        {
            List<Instruction> actual = PatchSites.BranchesTo(method.Body, target);
            if (actual.Count != sources.Length || sources.Any(s => !actual.Contains(s)))
            {
                throw new PatchException(method.FullName + ": IL_" + Hex(target) + " is the target of " +
                                         (actual.Count == 0 ? "no branch" : string.Join(", ", actual.Select(i => "IL_" + Hex(i)))) + ", expected " +
                                         (sources.Length == 0 ? "none" : string.Join(", ", sources.Select(i => "IL_" + Hex(i)))));
            }
        }

        private static string Hex(Instruction i)
        {
            return i.Offset.ToString("x4", CultureInfo.InvariantCulture);
        }

        /// <summary>The nearest parameterless instance <paramref name="name"/> declared up <paramref name="type"/>'s base chain.</summary>
        private static MethodDefinition NearestBaseMethod(TypeDefinition type, string name)
        {
            TypeReference baseType = type.BaseType;
            while (baseType != null)
            {
                TypeDefinition definition = baseType.Resolve();
                if (definition == null) throw new PatchException(type.FullName + ": cannot resolve the base type " + baseType.FullName);
                MethodDefinition method = definition.Methods.SingleOrDefault(m => m.Name == name && !m.IsStatic && !m.HasParameters);
                if (method != null) return method;
                baseType = definition.BaseType;
            }
            throw new PatchException(type.FullName + ": no base " + name + "()");
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
