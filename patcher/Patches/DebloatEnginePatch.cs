using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Optional 'debloat' patch of Gameloft.Engine.dll (default on): third-party SDKs that are dead offline are never started
    /// from managed code. Native offsets are of lib/arm64-v8a/libnativeengine.so of 1.17.32.
    /// <list type="bullet">
    /// <item>C1-C3 Crashlytics. <c>Crashlytics.Start()</c> (void) is called once, from <c>GameClientComponent.ProcessHandshakeReply</c>
    /// when the TS handshake says <c>EnableCrashlytics</c> (CRM <c>game.CrashlyticsParams.Enable</c>); nothing awaits it and
    /// <c>GameCrashlyticsComponent</c> only subscribes to <c>OnReady</c>. It now returns at once: <c>IsReady</c> stays false and
    /// <c>OnReady</c> never fires, as with Enable=false today. The only managed routes into the two native start exports,
    /// <c>Start_Native()</c> (its one caller is Start) and <c>Native_Start()</c> (no caller), return at once too. Both exports
    /// (0x39910c, 0x3990dc) tail-jump (via 0x3c5594/0x3c5598) to the one function (0x40efe8) that asks <c>CrashlyticAvailable</c> and calls Java
    /// <c>AndroidUtilsPlugin.CrashlyticStart</c> (0x40f024: <c>Fabric.with(Crashlytics, CrashlyticsNdk)</c>, which loads
    /// libcrashlytics.so); nothing else calls or references it, and managed <c>Fabric</c> has no start entry. So no CRM can
    /// start Fabric any more. Logging (<c>LogMessage</c>, <c>SetString</c>, <c>Fabric.LogLevelStart/End</c>) is unchanged and
    /// keeps ending in Java's <c>Fabric.isInitialized()</c> == false.</item>
    /// <item>N1-N4 remote push. <c>NotificationComponent.ShouldRegister()</c> returns false (no override in any shipped assembly;
    /// <c>DefaultValidator</c> and Game's <c>GamePNValidator</c> are no longer asked), so <c>RegisterForRemoteNotificationsAsync</c>
    /// takes its shipped "validation failure" path and returns a completed false before it waits for the connection (its
    /// callers: <c>GameNotificationBootstrapComponent.RegisterAsync</c> at boot, <c>SocialComponent.MergeOrSwitch</c>, a cheat; all
    /// fire-and-forget). <c>NativeNotificationSystem.RegisterForRemoteNotifications()</c>, the one caller of the
    /// <c>NativeNotificationSystem_RegisterForRemoteNotifications</c> P/Invoke (native: Java
    /// <c>SimplifiedAndroidUtils.GetDeviceToken</c>, the InstanceID token thread), and <c>Register()</c>, its one caller, do
    /// nothing. <c>RegisterAsync(ct)</c> posted Register() and then waited on <c>DeviceTokenGate</c>, which only a received token
    /// opens (none can arrive now; offline none ever did): it returns <c>Task.CompletedTask</c>. Its callers are the settings
    /// toggle (<c>GameNotificationComponent.OnPreferencesChanged</c>: fire-and-forget, then <c>System.Start()</c> and
    /// <c>RefreshAllNotifications()</c> as before) and RegisterForRemoteNotificationsAsync, where a finished wait with
    /// <c>DeviceToken == null</c> is the shipped "no token" result (false). Local notifications are untouched: the Scheduler,
    /// <c>ScheduleLocalNotification</c>/<c>Cancel*</c>, <c>Start</c>/<c>Pause</c> (the toggle, GameNotificationStarterComponent),
    /// badges, launch notifications, <c>Unregister</c>/<c>UnregisterAsync</c>.</item>
    /// <item>S1-S3 Facebook and Google Play Games at menu start. <c>SocialComponent.InitSocial</c> calls
    /// <c>SocialLib_private.Init(sns, sns != GameCenter || ShouldTriggerAutoLogin)</c> for every <c>Sns</c>. Passing
    /// triggerAutoLogin=false cannot defer the sign-in: the native export <c>SocialLib_private_Init_private</c> (0x3955a8) itself
    /// sets autoLogin = triggerAutoLogin || sns != GameCenter (0x3955c8 <c>cmp sns, #1</c>, 0x3955d0 <c>cset ne</c>, 0x395600 <c>orr</c>), the Init
    /// request stores lib.deferAutoLogIn = !autoLogin (0x54ffb0-0x54ffbc), and the Play Games lib's Init (vtable 0x165c5b0 -&gt;
    /// 0x53d4ac) passes that byte (0x53d4c8) to Java <c>GameAPIAndroidGLSocialLib.InitGameAPI(false, deferAutoLogIn)</c>
    /// (0x53b2cc-0x53b2d8), whose <c>GameHelper.onStart(activity, defer)</c> connects the GoogleApiClient unless defer
    /// (GameHelper.java:175-202; one automatic sign-in resolution prompt at most). The flag is therefore always set for
    /// Facebook and Play Games, so the least invasive managed change is not to init those two:
    /// <list type="bullet">
    /// <item>S1 <c>SocialLib_private.Init(sns, ...)</c> returns a completed <c>SnsResult.NotSent</c> for Facebook and
    /// GooglePlayGames without calling native: what it returns today for the Sns that snsconfig.json does not support on
    /// Android (GameCenter, Msn, SinaWeibo: the native Init logs "You didn't mark SNS ... as being supported" and returns -1).
    /// InitSocial goes on with the next Sns as before; its auto-login check reads only the native state queries
    /// <c>NeedsAutoLogin</c> and <c>IsLoggedIn</c>. Java <c>InitGameAPI</c> (GameHelper setup, GoogleApiClient, connect, sign-in
    /// prompt) and <c>FacebookAndroidGLSocialLib.Init</c> (FetchDeferredAppLinkData, auto-login) never run;
    /// <c>GameAPIAndroidGLSocialLib.onResume</c>, called at every resume, already copes with no GameHelper.</item>
    /// <item>S2/S3 <c>SocialLib_private.Login(sns, ct)</c> and <c>TriggerAutoLogin(sns, ct)</c> return a completed
    /// <c>SnsResult.Failed</c> for the two instead of throwing "SNS not initialized". <c>SocialComponent.Login</c> (the Facebook
    /// and Google Play buttons of the account settings through <c>UIComponent.NativeLogin</c>, and a cheat) sends
    /// <c>Connecting</c> then <c>Failed</c> (SOCIAL_LOGIN_RESULT with the failure popup: what a tap ends with today offline,
    /// after the external login failed), never a lone <c>Connecting</c>; no Game.dll change is needed.</item>
    /// </list>
    /// Every other entry point stays as it is: the managed <c>GetUid</c>, <c>GetName</c>, <c>Logout</c>, <c>PostToWall</c>,
    /// <c>GetFriends</c> and <c>GotAchievement</c> require <c>IsLoggedIn</c>, false today as well (these logins cannot succeed
    /// offline); <c>GetAuthorizationCode</c> runs only after a successful login. Natively, every request export but Init (Login,
    /// TriggerAutoLogin, Logout, GetUid, GetUserName, GetFriends, PostToWall, GotAchievement, GetAuthorizationCode,
    /// ShowGoogleAchievements) first calls ClientSNSInterface's gate (0x54e374), which refuses an Sns "never initialized using
    /// initSNS()" (the request comes back as NotSent); the Java login, share and game-request dialogs are request handlers of
    /// the per-Sns libs (game request: vtable 0x165c3c0 -&gt; 0x535070), reached only through queued requests.
    /// <c>BrowserComponent_ShowFacebookShare/Page</c>, <c>StopGPGames</c> and <c>ShowGooglePlusOneButton</c> are empty on Android
    /// and <c>FriendsComponent.SendFacebookGameRequest</c> has no managed caller. Not changed: <c>SocialComponent.TriggerAutoLogin</c>
    /// waits for the Sns's init and would wait forever for these two, but no shipped assembly calls it.</item>
    /// </list>
    /// Every patched method, and every state machine or caller whose shipped behaviour the changes rely on, is checked against
    /// the IL fingerprint of the verified 1.17.32 build first; the enum values come from the assembly.
    /// </summary>
    public sealed class DebloatEnginePatch : IlPatch
    {
        public const string CrashlyticsType = "Gameloft.Engine.Crashlytics";
        public const string NotificationType = "Gameloft.Engine.NotificationComponent";
        public const string NotificationSystemType = "Gameloft.Engine.NativeNotificationSystem";
        public const string SocialLibType = "Gameloft.Engine.SocialLib_private";
        public const string SocialType = "Gameloft.Engine.SocialComponent";
        public const string SnsType = "Gameloft.Engine.Sns";
        public const string SnsResultType = "Gameloft.Engine.SnsResult";

        public const string StartFingerprint = "1354ab8ba7b01698c88937ff81c4e0067e1dd401b4bcd0d9783557307499e27b";
        public const string StartNativeFingerprint = "33eee03e57209fa6a62fbef6550be46cc60c6310780ce2cbf816b2068aeede57";
        public const string NativeStartFingerprint = "c0b541f2b3df19b95aede4442d14823a2fa7e3ce85c5992fd01622802909035f";
        public const string ShouldRegisterFingerprint = "6aed99d35b7023eeab45e100285e72300b6ca5da21dafa7a0cb33806f0ec6a3d";
        public const string RegisterFingerprint = "cf9513931c5a98628c180f7b99ea7980c6470855fbcbf35a4c867ef52bdf6c99";
        public const string RegisterForRemoteFingerprint = "ee551b9ba62f02379878bb67bc3c4dc2be5e96d91e8932d4ee074e862fd08160";
        public const string RegisterAsyncFingerprint = "da5cea03a9221172a5bd7e2561d0a40cb9766964da91ceb6704dbc9d9aabacc7";
        public const string InitFingerprint = "05d59c00e67a99a89bb042c1fe84baca97e9b8a27ce3871886e49fe4b00f8430";
        public const string LoginFingerprint = "bdae2c19a29e993786175b3dd5356f3f8dc78b1878918eb5bab0b64d713c6165";
        public const string TriggerAutoLoginFingerprint = "d22a38f0144842597ed4b0facc15cd7e829f6bae4b7a71744ad012d50895ac9b";

        // The state machines (MoveNext) the changes bypass or rely on, unchanged.
        public const string RegisterAsyncBodyFingerprint = "0191bea9ec833258fc52a347ba60d316e719bf5b0006703e2e17f9cea2cf0875";
        public const string RegisterRemoteBodyFingerprint = "450c815d595206f16905bbb6a7375939d1af6d938a2faf1ebafefe6e096899b7";
        public const string InitBodyFingerprint = "7d89d12548e0a90bd98f73c8bfc5394e227d1ba9035479b6301274de644d7611";
        public const string LoginBodyFingerprint = "90ade6b43c11953ea5eb19df2edea570f3f902e62813f32e90c02f1a455a6901";
        public const string TriggerAutoLoginBodyFingerprint = "4116de68a96ee9aeac843beeba96666c583c4ac722a8ab5e4c455a47b9c5a449";
        public const string InitSocialBodyFingerprint = "8458f7e95bd7a6811128ccadc420eb8ee104673125f4050fd132eebd97872eb1";
        public const string LoginImplBodyFingerprint = "4e4b7acbfe88d419ab7e2512cb8ae9d81a4f589ee7fc3082a5e483729ead9414";

        public override string Id => "debloat";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "dead SDKs: Crashlytics never starts, no push-token registration, no Facebook/Play Games init or automatic sign-in (Gameloft.Engine.dll)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify: Crashlytics
            TypeDefinition crashlytics = RequireType(module, CrashlyticsType);
            MethodDefinition start = RequireMethod(crashlytics, "Start", m => !m.IsStatic && !m.HasParameters && IsVoid(m), "instance void Start()");
            MethodDefinition startNative = RequireMethod(crashlytics, "Start_Native", m => m.IsStatic && !m.HasParameters && IsVoid(m), "static void Start_Native()");
            MethodDefinition nativeStart = RequireMethod(crashlytics, "Native_Start", m => !m.IsStatic && !m.HasParameters && IsVoid(m), "instance void Native_Start()");
            RequireFingerprint(start, StartFingerprint, "Crashlytics.Start()");
            RequireFingerprint(startNative, StartNativeFingerprint, "Crashlytics.Start_Native()");
            RequireFingerprint(nativeStart, NativeStartFingerprint, "Crashlytics.Native_Start()");
            RequireOnlyCaller(module, crashlytics, "Crashlytics_Start_Native", startNative);
            RequireOnlyCaller(module, crashlytics, "Crashlytics_Native_Start", nativeStart);

            // ---- push
            TypeDefinition notification = RequireType(module, NotificationType);
            TypeDefinition system = RequireType(module, NotificationSystemType);
            MethodDefinition shouldRegister = RequireMethod(notification, "ShouldRegister",
                m => !m.IsStatic && m.IsVirtual && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.Boolean, "virtual bool ShouldRegister()");
            MethodDefinition registerRemoteAsync = RequireMethod(notification, "RegisterForRemoteNotificationsAsync",
                m => !m.IsStatic && m.Parameters.Count == 1, "Task<bool> RegisterForRemoteNotificationsAsync(CancellationToken)");
            MethodDefinition register = RequireMethod(system, "Register", m => !m.IsStatic && !m.HasParameters && IsVoid(m), "void Register()");
            MethodDefinition registerRemote = RequireMethod(system, "RegisterForRemoteNotifications",
                m => !m.IsStatic && !m.HasParameters && IsVoid(m), "void RegisterForRemoteNotifications()");
            MethodDefinition registerAsync = RequireMethod(system, "RegisterAsync",
                m => !m.IsStatic && m.Parameters.Count == 1 && m.ReturnType.FullName == "System.Threading.Tasks.Task", "Task RegisterAsync(CancellationToken)");
            RequireFingerprint(shouldRegister, ShouldRegisterFingerprint, "NotificationComponent.ShouldRegister()");
            RequireFingerprint(StateMachine(registerRemoteAsync), RegisterRemoteBodyFingerprint, "NotificationComponent.RegisterForRemoteNotificationsAsync state machine");
            RequireFingerprint(register, RegisterFingerprint, "NativeNotificationSystem.Register()");
            RequireFingerprint(registerRemote, RegisterForRemoteFingerprint, "NativeNotificationSystem.RegisterForRemoteNotifications()");
            RequireFingerprint(registerAsync, RegisterAsyncFingerprint, "NativeNotificationSystem.RegisterAsync(CancellationToken)");
            RequireFingerprint(StateMachine(registerAsync), RegisterAsyncBodyFingerprint, "NativeNotificationSystem.RegisterAsync state machine");
            RequireOnlyCaller(module, system, "NativeNotificationSystem_RegisterForRemoteNotifications", registerRemote);

            // ---- social
            TypeDefinition socialLib = RequireType(module, SocialLibType);
            TypeDefinition social = RequireType(module, SocialType);
            TypeDefinition sns = RequireType(module, SnsType);
            TypeDefinition snsResult = RequireType(module, SnsResultType);
            MethodDefinition init = RequireMethod(socialLib, "Init", m => !m.IsStatic && Takes(m, SnsType, "System.Boolean", "System.Threading.CancellationToken") && ReturnsResult(m),
                                                  "Task<SnsResult> Init(Sns, bool, CancellationToken)");
            MethodDefinition login = RequireMethod(socialLib, "Login", m => !m.IsStatic && Takes(m, SnsType, "System.Threading.CancellationToken") && ReturnsResult(m),
                                                   "Task<SnsResult> Login(Sns, CancellationToken)");
            MethodDefinition autoLogin = RequireMethod(socialLib, "TriggerAutoLogin", m => !m.IsStatic && Takes(m, SnsType, "System.Threading.CancellationToken") && ReturnsResult(m),
                                                       "Task<SnsResult> TriggerAutoLogin(Sns, CancellationToken)");
            MethodDefinition initSocial = RequireMethod(social, "InitSocial", m => !m.IsStatic && m.Parameters.Count == 1, "Task InitSocial(CancellationToken)");
            MethodDefinition loginImpl = RequireMethod(social, "LoginImpl", m => !m.IsStatic && Takes(m, SnsType, "System.Threading.CancellationToken"),
                                                       "Task<SnsResult> LoginImpl(Sns, CancellationToken)");
            RequireFingerprint(init, InitFingerprint, "SocialLib_private.Init(Sns, bool, CancellationToken)");
            RequireFingerprint(login, LoginFingerprint, "SocialLib_private.Login(Sns, CancellationToken)");
            RequireFingerprint(autoLogin, TriggerAutoLoginFingerprint, "SocialLib_private.TriggerAutoLogin(Sns, CancellationToken)");
            RequireFingerprint(StateMachine(init), InitBodyFingerprint, "SocialLib_private.Init state machine");
            RequireFingerprint(StateMachine(login), LoginBodyFingerprint, "SocialLib_private.Login state machine");
            RequireFingerprint(StateMachine(autoLogin), TriggerAutoLoginBodyFingerprint, "SocialLib_private.TriggerAutoLogin state machine");
            RequireFingerprint(StateMachine(initSocial), InitSocialBodyFingerprint, "SocialComponent.InitSocial state machine");
            RequireFingerprint(StateMachine(loginImpl), LoginImplBodyFingerprint, "SocialComponent.LoginImpl state machine");
            int facebook = EnumValue(sns, "Facebook"), playGames = EnumValue(sns, "GooglePlayGames");
            int notSent = EnumValue(snsResult, "NotSent"), failed = EnumValue(snsResult, "Failed");

            // ---- references, each checked against the game's own BCL
            TypeReference task = registerAsync.ReturnType;
            MethodReference completedTask = CorlibMethod(module, task, "get_CompletedTask", false, task);
            RequireCreatedOnce(completedTask.Resolve());
            MethodReference fromResult = FromResult(module, (GenericInstanceType)init.ReturnType);

            // ---- change: C1-C3
            foreach (MethodDefinition method in new[] { start, startNative, nativeStart }) ReturnAtOnce(method);
            context.Notes.Add(start.FullName + ": returns at once (Crashlytics/Fabric never start, whatever the handshake's EnableCrashlytics says)");
            context.Notes.Add(startNative.FullName + ", " + nativeStart.FullName + ": return at once (the only managed routes to Java " +
                              "AndroidUtilsPlugin.CrashlyticStart and libcrashlytics.so)");

            // ---- change: N1-N4
            ReturnAtOnce(shouldRegister, Instruction.Create(OpCodes.Ldc_I4_0));
            context.Notes.Add(shouldRegister.FullName + ": false (RegisterForRemoteNotificationsAsync returns false before it waits for the connection)");
            ReturnAtOnce(registerRemote);
            ReturnAtOnce(register);
            context.Notes.Add(registerRemote.FullName + ", " + register.FullName + ": do nothing (no push-token request)");
            ReturnAtOnce(registerAsync, Instruction.Create(OpCodes.Call, completedTask));
            context.Notes.Add(registerAsync.FullName + ": Task.CompletedTask (no Register() post, no wait for a device token that cannot come)");

            // ---- change: S1-S3
            ResultFor(init, facebook, playGames, notSent, fromResult);
            context.Notes.Add(init.FullName + ": Facebook and GooglePlayGames -> NotSent without native init (no GoogleApiClient connect or sign-in " +
                              "prompt, no Facebook init or auto-login; the result of an Sns snsconfig does not support)");
            ResultFor(login, facebook, playGames, failed, fromResult);
            ResultFor(autoLogin, facebook, playGames, failed, fromResult);
            context.Notes.Add(login.FullName + ", " + autoLogin.FullName + ": Facebook and GooglePlayGames -> Failed (a login tap fails as " +
                              "it does offline today instead of throwing 'SNS not initialized')");
        }

        private static bool IsVoid(MethodDefinition m) => m.ReturnType.MetadataType == MetadataType.Void;

        private static bool Takes(MethodDefinition m, params string[] types) =>
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(types);

        private static bool ReturnsResult(MethodDefinition m) =>
            m.ReturnType.FullName == "System.Threading.Tasks.Task`1<" + SnsResultType + ">";

        /// <summary>The MoveNext of an async method's state machine (named by its [AsyncStateMachine] attribute).</summary>
        private static MethodDefinition StateMachine(MethodDefinition method)
        {
            CustomAttribute attribute = method.CustomAttributes.SingleOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
            TypeDefinition machine = attribute != null && attribute.ConstructorArguments.Count == 1 && attribute.ConstructorArguments[0].Value is TypeReference t
                ? t.Resolve() : null;
            if (machine == null || machine.DeclaringType != method.DeclaringType)
                throw new PatchException(method.FullName + " is not the verified async method (no state machine of its own)");
            return RequireMethod(machine, "MoveNext", m => !m.IsStatic && !m.HasParameters, "MoveNext()");
        }

        /// <summary>Throws unless the private P/Invoke <paramref name="name"/> of <paramref name="type"/> is called by <paramref name="caller"/> alone.</summary>
        private static void RequireOnlyCaller(ModuleDefinition module, TypeDefinition type, string name, MethodDefinition caller)
        {
            MethodDefinition native = type.Methods.SingleOrDefault(m => m.Name == name);
            if (native == null || !native.IsPInvokeImpl || !native.IsPrivate || native.PInvokeInfo.EntryPoint != name)
                throw new PatchException(type.FullName + " has no private P/Invoke " + name);
            var callers = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference r &&
                                                                                    r.Name == name && r.DeclaringType.FullName == type.FullName))
                .ToList();
            if (callers.Count != 1 || callers[0] != caller)
                throw new PatchException(name + " is called by " + string.Join(", ", callers.Select(m => m.FullName)) + ", expected " + caller.FullName + " alone");
        }

        private static int EnumValue(TypeDefinition type, string name)
        {
            FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == name && f.IsStatic && f.HasConstant);
            if (!type.IsEnum || field == null || !(field.Constant is int value)) throw new PatchException(type.FullName + " has no member " + name);
            return value;
        }

        /// <summary>
        /// The game's <c>Task.CompletedTask</c> is an auto-property its static constructor sets to a new task (a caller that awaited
        /// a null task would throw): <c>ldsfld &lt;CompletedTask&gt;k__BackingField; ret</c>, and <c>.cctor</c> stores a <c>newobj</c> there.
        /// </summary>
        private static void RequireCreatedOnce(MethodDefinition getter)
        {
            var code = getter.Body.Instructions;
            var backing = code.Count == 2 && code[0].OpCode == OpCodes.Ldsfld && code[1].OpCode == OpCodes.Ret ? (FieldReference)code[0].Operand : null;
            MethodDefinition cctor = getter.DeclaringType.Methods.SingleOrDefault(m => m.IsConstructor && m.IsStatic && m.HasBody);
            bool created = backing != null && cctor != null &&
                           cctor.Body.Instructions.Count(i => i.OpCode == OpCodes.Stsfld && ((FieldReference)i.Operand).Name == backing.Name) == 1 &&
                           cctor.Body.Instructions.Any(i => i.OpCode == OpCodes.Stsfld && ((FieldReference)i.Operand).Name == backing.Name &&
                                                            i.Previous != null && i.Previous.OpCode == OpCodes.Newobj &&
                                                            ((MethodReference)i.Previous.Operand).DeclaringType.FullName == "System.Threading.Tasks.Task");
            if (!created) throw new PatchException("the game's mscorlib: Task.CompletedTask is not the task its static constructor creates:\n" + Dump(getter));
        }

        /// <summary>
        /// <c>Task.FromResult&lt;SnsResult&gt;</c>: the open <c>Task.FromResult&lt;TResult&gt;</c> the module already calls (the game's
        /// mscorlib), instantiated for the result type of <paramref name="taskOfResult"/>.
        /// </summary>
        private static MethodReference FromResult(ModuleDefinition module, GenericInstanceType taskOfResult)
        {
            MethodReference open = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                .Select(i => i.Operand as GenericInstanceMethod)
                .Where(g => g != null && g.Name == "FromResult" && g.DeclaringType.FullName == "System.Threading.Tasks.Task" && g.GenericArguments.Count == 1)
                .Select(g => g.ElementMethod)
                .FirstOrDefault();
            MethodDefinition resolved;
            try
            {
                resolved = open?.Resolve();
            }
            catch (AssemblyResolutionException ex)
            {
                throw new PatchException("cannot resolve the game's mscorlib for Task.FromResult: " + ex.Message);
            }
            if (resolved == null || !resolved.IsStatic || resolved.GenericParameters.Count != 1 || resolved.Parameters.Count != 1)
                throw new PatchException(module.Name + " calls no Task.FromResult<T>(T) of the game's mscorlib");
            var closed = new GenericInstanceMethod(open);
            closed.GenericArguments.Add(taskOfResult.GenericArguments[0]);
            return closed;
        }

        /// <summary>Prepends <c>[value;] ret</c>: the shipped body stays in place behind it, unreachable.</summary>
        private static void ReturnAtOnce(MethodDefinition method, Instruction value = null)
        {
            method.Body.SimplifyMacros();
            ILProcessor il = method.Body.GetILProcessor();
            Instruction first = method.Body.Instructions[0];
            if (value != null) il.InsertBefore(first, value);
            il.InsertBefore(first, il.Create(OpCodes.Ret));
            method.Body.OptimizeMacros();
        }

        /// <summary>
        /// <code>
        /// if (sns == Sns.Facebook || sns == Sns.GooglePlayGames) return Task.FromResult((SnsResult)result);
        /// </code>
        /// in front of the async stub (the state machine is not started for those two).
        /// </summary>
        private static void ResultFor(MethodDefinition method, int facebook, int playGames, int result, MethodReference fromResult)
        {
            method.Body.SimplifyMacros();
            ILProcessor il = method.Body.GetILProcessor();
            Instruction original = method.Body.Instructions[0];
            Instruction skip = il.Create(OpCodes.Ldc_I4, result);
            ParameterDefinition sns = method.Parameters[0];
            foreach (Instruction i in new[]
            {
                il.Create(OpCodes.Ldarg, sns), il.Create(OpCodes.Ldc_I4, facebook), il.Create(OpCodes.Beq, skip),
                il.Create(OpCodes.Ldarg, sns), il.Create(OpCodes.Ldc_I4, playGames), il.Create(OpCodes.Bne_Un, original),
                skip, il.Create(OpCodes.Call, fromResult), il.Create(OpCodes.Ret),
            })
            {
                il.InsertBefore(original, i);
            }
            method.Body.OptimizeMacros();
        }
    }
}
