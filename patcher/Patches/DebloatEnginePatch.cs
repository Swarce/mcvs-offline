using System.Collections.Generic;
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
    /// fire-and-forget). <c>NativeNotificationSystem.RegisterForRemoteNotifications()</c> is the one caller of the
    /// <c>NativeNotificationSystem_RegisterForRemoteNotifications</c> P/Invoke, whose export (0x3966d4 -&gt; 0x40d60c) does two
    /// things: first the local-alarm purge of <c>NativeNotificationSystem_CancelAllLocalNotifications</c> (0x3966c8 -&gt; 0x40d830:
    /// DeleteMessageGroup through JNI (0xec5870) for the groups 1..0x13, then the native map at 0x1702aa8 cleared; the two
    /// sequences are the same instructions, 0x40d624-0x40d670 and 0x40d840-0x40d884, and 0x40d830 has no other caller), then
    /// the token request (Java <c>SimplifiedAndroidUtils.GetDeviceToken</c>, the InstanceID token thread). It now calls the
    /// managed <c>CancelAllLocalNotifications()</c> (the one caller of that P/Invoke: the same self check, then 0x40d830) and
    /// returns, so only the token request is dropped; <c>Register()</c>, its one caller, is unchanged. <c>RegisterAsync(ct)</c>
    /// posted Register() to <c>GameApplication.Instance.Context</c> (through the shipped lambda) and then waited on
    /// <c>DeviceTokenGate</c>, which only a received token opens (none can arrive now; offline none ever did): it still posts
    /// Register() there (<c>McvsDebloat.Post</c>: when there is a GameApplication with a context) and returns
    /// <c>Task.CompletedTask</c>. Its callers are the settings toggle
    /// (<c>GameNotificationComponent.OnPreferencesChanged</c>: fire-and-forget, then <c>System.Start()</c> and
    /// <c>RefreshAllNotifications()</c>, whose Scheduler work is posted after Register(), as before) and
    /// RegisterForRemoteNotificationsAsync, where a finished wait with <c>DeviceToken == null</c> is the shipped "no token"
    /// result (false). The rest of the local notifications is untouched: the Scheduler, <c>ScheduleLocalNotification</c>/
    /// <c>Cancel*</c>, <c>Start</c>/<c>Pause</c> (the toggle, GameNotificationStarterComponent), badges, launch notifications,
    /// <c>Unregister</c>/<c>UnregisterAsync</c>. The boot registration no longer reaches Register(), so its late purge (after the
    /// connection wait) is gone; <c>GameNotificationComponent.OnStart</c> still posts its own CancelAllLocalNotifications().</item>
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
    /// <item>S2/S3 <c>SocialLib_private.Login(sns, ct)</c> and <c>TriggerAutoLogin(sns, ct)</c> return, for the two, a task that
    /// completes with <c>SnsResult.Failed</c> on a later turn of the game thread's work queue instead of throwing "SNS not
    /// initialized": <c>McvsDebloat.FailLater()</c> posts the completion to <c>GameApplication.Instance.Context</c> (the
    /// <c>GameSynchronizationContext</c> whose <c>Post</c> only enqueues; <c>UpdateWorlds</c> runs its items on the game thread
    /// each frame, the queue <c>NativeNotificationSystem.RegisterAsync</c> and <c>Scheduler.Schedule</c> use), and completes it at
    /// once only without a GameApplication or context. <c>SocialComponent.Login</c> (the Facebook and Google Play buttons of
    /// the account settings through <c>UIComponent.NativeLogin</c>, and a cheat) therefore sends <c>Connecting</c>, returns to
    /// the UI (whose <c>MenuAccountSettings.doAction</c> calls <c>SocialButton.connecting()</c> after <c>Native.Login</c>), and
    /// sends <c>Failed</c> on a later frame: LoginImpl's await resumes, exactly once, on the context it captured (inline in the
    /// completing item when that is GameApplication's; a menu's Flash calls run in a World update, so it is that World's, and
    /// the completing item posts the continuation there for the World's next update), unmutes the sounds and sends
    /// SOCIAL_LOGIN_RESULT with the failure popup, whose handler resets the button, as a native result delivered by
    /// <c>SocialComponent.OnUpdate</c> did. Never a lone <c>Connecting</c>; no Game.dll change is needed.</item>
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
    /// The patch adds one internal static class, <c>Gameloft.Engine.McvsDebloat</c> (<c>Post</c>, <c>FailLater</c>, <c>Fail</c>),
    /// built from the references of the verified RegisterAsync and from BCL members checked against the game's own mscorlib.
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
        public const string ApplicationType = "Gameloft.Engine.GameApplication";
        public const string GameContextType = "Gameloft.Engine.GameSynchronizationContext";

        /// <summary>The type the patch adds to Gameloft.Engine.dll (namespace Gameloft.Engine).</summary>
        public const string HelperType = "McvsDebloat";

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

        // Unchanged methods the changes call or rely on.
        public const string CancelAllFingerprint = "ce6a91cca50a0f5c869ff701d137ee7f0a4238a5c7e20f915ee93d65a5f4dbcd";
        public const string RegisterLambdaFingerprint = "90c6826894802554d9c489b4edaf26039e042fd4f9dad2bb0fa6ee336c3fe80b";
        public const string ContextPostFingerprint = "b627f95332ce6a1ef5c7a12e8974eeda3d8bf7f06e79279858a6abb0b3b01331";

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
            ForBuild(context, Id, null);   // Android only: excluded on PC (docs/design/pc-port.md §8.8)
            PatchSites.RequireBuildAssembly(module, context, Id);
            if (module.GetType("Gameloft.Engine." + HelperType) != null)
                throw new PatchException("Gameloft.Engine.dll is already patched: run the patcher on your original APK");

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
            MethodDefinition cancelAll = RequireMethod(system, "CancelAllLocalNotifications", m => !m.IsStatic && !m.HasParameters && IsVoid(m),
                                                       "void CancelAllLocalNotifications()");
            RequireFingerprint(cancelAll, CancelAllFingerprint, "NativeNotificationSystem.CancelAllLocalNotifications()");
            RequireOnlyCaller(module, system, "NativeNotificationSystem_CancelAllLocalNotifications", cancelAll);

            // RegisterAsync's post (its state machine, fingerprinted above): IL_000a call GameApplication.get_Instance();
            // IL_000f callvirt get_Context(); IL_0014 ldarg.0; IL_0015 ldfld <>4__this; IL_001a ldftn <RegisterAsync>b__* (this.Register());
            // IL_0020 newobj SendOrPostCallback(object, IntPtr); IL_0025 ldnull; IL_0026 callvirt SynchronizationContext.Post. The
            // changes reuse these references and that lambda.
            var posting = StateMachine(registerAsync).Body.Instructions;
            Instruction PostSite(int offset) => posting.Single(i => i.Offset == offset);
            var getInstance = PostSite(0x0a).Operand as MethodReference;
            var getContext = PostSite(0x0f).Operand as MethodReference;
            var registerLambda = (PostSite(0x1a).Operand as MethodReference)?.Resolve();
            var callbackCtor = PostSite(0x20).Operand as MethodReference;
            var contextPost = PostSite(0x26).Operand as MethodReference;
            if (PostSite(0x0a).OpCode != OpCodes.Call || getInstance?.Name != "get_Instance" || getInstance.DeclaringType.FullName != ApplicationType ||
                PostSite(0x0f).OpCode != OpCodes.Callvirt || getContext?.Name != "get_Context" || getContext.ReturnType.FullName != GameContextType ||
                PostSite(0x1a).OpCode != OpCodes.Ldftn || registerLambda == null || registerLambda.DeclaringType != system || registerLambda.IsStatic ||
                PostSite(0x20).OpCode != OpCodes.Newobj || callbackCtor?.DeclaringType.FullName != "System.Threading.SendOrPostCallback" ||
                PostSite(0x25).OpCode != OpCodes.Ldnull ||
                PostSite(0x26).OpCode != OpCodes.Callvirt || contextPost?.Name != "Post" || contextPost.DeclaringType.FullName != "System.Threading.SynchronizationContext")
                throw new PatchException(registerAsync.FullName + ": the verified Context.Post(Register) site moved");
            RequireFingerprint(registerLambda, RegisterLambdaFingerprint, "NativeNotificationSystem.RegisterAsync's lambda (this.Register())");
            // GameApplication.Context is a GameSynchronizationContext whose Post only enqueues (the items run in UpdateWorlds).
            MethodDefinition gamePost = RequireMethod(RequireType(module, GameContextType), "Post", m => !m.IsStatic && m.IsVirtual && !m.IsNewSlot &&
                                                      m.Parameters.Count == 2 && IsVoid(m), "override void Post(SendOrPostCallback, object)");
            RequireFingerprint(gamePost, ContextPostFingerprint, "GameSynchronizationContext.Post(SendOrPostCallback, object)");

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
            TypeSystem ts = module.TypeSystem;
            var sourceOpen = new TypeReference("System.Threading.Tasks", "TaskCompletionSource`1", module, ts.CoreLibrary);
            sourceOpen.GenericParameters.Add(new GenericParameter("TResult", sourceOpen));
            var source = new GenericInstanceType(sourceOpen);
            source.GenericArguments.Add(snsResult);
            var taskOfT = new GenericInstanceType(new TypeReference("System.Threading.Tasks", "Task`1", module, ts.CoreLibrary));
            taskOfT.GenericArguments.Add(sourceOpen.GenericParameters[0]);
            MethodReference sourceCtor = Checked(module, new MethodReference(".ctor", ts.Void, source) { HasThis = true });
            MethodReference sourceTask = Checked(module, new MethodReference("get_Task", taskOfT, source) { HasThis = true });
            var trySetResult = new MethodReference("TrySetResult", ts.Boolean, source) { HasThis = true };
            trySetResult.Parameters.Add(new ParameterDefinition(sourceOpen.GenericParameters[0]));
            trySetResult = Checked(module, trySetResult);
            Checked(module, callbackCtor);
            Checked(module, contextPost);

            // ---- the helper type
            TypeDefinition helper = AddHelperType(module, getInstance, getContext, contextPost, callbackCtor, module.ImportReference(source), sourceCtor,
                                                  sourceTask, trySetResult, init.ReturnType, failed);
            MethodDefinition post = helper.Methods.Single(m => m.Name == "Post");
            MethodDefinition failLater = helper.Methods.Single(m => m.Name == "FailLater");

            // ---- change: C1-C3
            foreach (MethodDefinition method in new[] { start, startNative, nativeStart }) ReturnAtOnce(method);
            context.Notes.Add(start.FullName + ": returns at once (Crashlytics/Fabric never start, whatever the handshake's EnableCrashlytics says)");
            context.Notes.Add(startNative.FullName + ", " + nativeStart.FullName + ": return at once (the only managed routes to Java " +
                              "AndroidUtilsPlugin.CrashlyticStart and libcrashlytics.so)");

            // ---- change: N1-N4
            ReturnAtOnce(shouldRegister, Instruction.Create(OpCodes.Ldc_I4_0));
            context.Notes.Add(shouldRegister.FullName + ": false (RegisterForRemoteNotificationsAsync returns false before it waits for the connection)");
            ReturnAtOnce(registerRemote, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Callvirt, cancelAll));
            context.Notes.Add(registerRemote.FullName + ": CancelAllLocalNotifications() only (the local-alarm purge the native export runs " +
                              "first), no push-token request; Register() still calls it");
            ReturnAtOnce(registerAsync, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldftn, registerLambda),
                         Instruction.Create(OpCodes.Newobj, callbackCtor), Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Call, post),
                         Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Call, completedTask));
            context.Notes.Add(registerAsync.FullName + ": posts Register() to GameApplication.Context as shipped (when there is one), then " +
                              "Task.CompletedTask (no wait for a device token that cannot come)");

            // ---- change: S1-S3
            ResultFor(init, facebook, playGames, Instruction.Create(OpCodes.Ldc_I4, notSent), Instruction.Create(OpCodes.Call, fromResult));
            context.Notes.Add(init.FullName + ": Facebook and GooglePlayGames -> NotSent without native init (no GoogleApiClient connect or sign-in " +
                              "prompt, no Facebook init or auto-login; the result of an Sns snsconfig does not support)");
            ResultFor(login, facebook, playGames, Instruction.Create(OpCodes.Call, failLater));
            ResultFor(autoLogin, facebook, playGames, Instruction.Create(OpCodes.Call, failLater));
            context.Notes.Add(login.FullName + ", " + autoLogin.FullName + ": Facebook and GooglePlayGames -> Failed on a later turn of " +
                              "GameApplication.Context (Gameloft.Engine." + HelperType + ".FailLater; at once without one): a login tap fails as " +
                              "it does offline today, after the UI has shown Connecting, instead of throwing 'SNS not initialized'");
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

        /// <summary>Prepends <c>[code;] ret</c>: the shipped body stays in place behind it, unreachable.</summary>
        private static void ReturnAtOnce(MethodDefinition method, params Instruction[] code)
        {
            method.Body.SimplifyMacros();
            ILProcessor il = method.Body.GetILProcessor();
            Instruction first = method.Body.Instructions[0];
            foreach (Instruction i in code) il.InsertBefore(first, i);
            il.InsertBefore(first, il.Create(OpCodes.Ret));
            method.Body.OptimizeMacros();
        }

        /// <summary>
        /// <code>
        /// if (sns == Sns.Facebook || sns == Sns.GooglePlayGames) return &lt;result&gt;;
        /// </code>
        /// in front of the async stub (the state machine is not started for those two); <paramref name="result"/> leaves the
        /// <c>Task&lt;SnsResult&gt;</c> on the stack.
        /// </summary>
        private static void ResultFor(MethodDefinition method, int facebook, int playGames, params Instruction[] result)
        {
            method.Body.SimplifyMacros();
            ILProcessor il = method.Body.GetILProcessor();
            Instruction original = method.Body.Instructions[0];
            Instruction skip = result[0];
            ParameterDefinition sns = method.Parameters[0];
            var code = new List<Instruction>
            {
                il.Create(OpCodes.Ldarg, sns), il.Create(OpCodes.Ldc_I4, facebook), il.Create(OpCodes.Beq, skip),
                il.Create(OpCodes.Ldarg, sns), il.Create(OpCodes.Ldc_I4, playGames), il.Create(OpCodes.Bne_Un, original),
            };
            code.AddRange(result);
            code.Add(il.Create(OpCodes.Ret));
            foreach (Instruction i in code) il.InsertBefore(original, i);
            method.Body.OptimizeMacros();
        }

        /// <summary>Imports <paramref name="reference"/> after checking that the game's assemblies define it.</summary>
        private static MethodReference Checked(ModuleDefinition module, MethodReference reference)
        {
            MethodDefinition resolved;
            try
            {
                resolved = reference.Resolve();
            }
            catch (AssemblyResolutionException ex)
            {
                throw new PatchException("cannot resolve the game's assemblies for " + reference.FullName + ": " + ex.Message);
            }
            if (resolved == null) throw new PatchException("the game's assemblies have no " + reference.FullName);
            return module.ImportReference(reference);
        }

        /// <summary>
        /// <code>
        /// static class McvsDebloat
        /// {
        ///     // callback(state) on a later turn of the game thread's work queue; false (nothing posted) without one.
        ///     internal static bool Post(SendOrPostCallback callback, object state)
        ///     {
        ///         GameApplication application = GameApplication.Instance;
        ///         if (application == null) return false;
        ///         GameSynchronizationContext context = application.Context;
        ///         if (context == null) return false;
        ///         context.Post(callback, state);
        ///         return true;
        ///     }
        ///
        ///     // Failed, once the items queued before it have run (at once without a queue).
        ///     internal static Task&lt;SnsResult&gt; FailLater()
        ///     {
        ///         var source = new TaskCompletionSource&lt;SnsResult&gt;();
        ///         if (!Post(Fail, source)) Fail(source);
        ///         return source.Task;
        ///     }
        ///
        ///     private static void Fail(object source) =&gt; ((TaskCompletionSource&lt;SnsResult&gt;)source).TrySetResult(SnsResult.Failed);
        /// }
        /// </code>
        /// </summary>
        private static TypeDefinition AddHelperType(ModuleDefinition module, MethodReference getInstance, MethodReference getContext, MethodReference contextPost,
                                                    MethodReference callbackCtor, TypeReference source, MethodReference sourceCtor, MethodReference sourceTask,
                                                    MethodReference trySetResult, TypeReference taskOfResult, int failed)
        {
            TypeSystem ts = module.TypeSystem;
            var helper = new TypeDefinition("Gameloft.Engine", HelperType, TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed |
                                                                           TypeAttributes.BeforeFieldInit | TypeAttributes.Class, ts.Object);
            module.Types.Add(helper);

            var post = new MethodDefinition("Post", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Boolean);
            post.Parameters.Add(new ParameterDefinition("callback", ParameterAttributes.None, callbackCtor.DeclaringType));
            post.Parameters.Add(new ParameterDefinition("state", ParameterAttributes.None, ts.Object));
            {
                ILProcessor il = post.Body.GetILProcessor();
                Instruction haveApplication = il.Create(OpCodes.Callvirt, getContext);
                Instruction haveContext = il.Create(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, getInstance);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Brtrue, haveApplication);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ret);
                il.Append(haveApplication);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Brtrue, haveContext);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ret);
                il.Append(haveContext);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, contextPost);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ret);
                post.Body.OptimizeMacros();
            }
            helper.Methods.Add(post);

            var fail = new MethodDefinition("Fail", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
            fail.Parameters.Add(new ParameterDefinition("source", ParameterAttributes.None, ts.Object));
            {
                ILProcessor il = fail.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, source);
                il.Emit(OpCodes.Ldc_I4, failed);
                il.Emit(OpCodes.Callvirt, trySetResult);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ret);
                fail.Body.OptimizeMacros();
            }
            helper.Methods.Add(fail);

            var failLater = new MethodDefinition("FailLater", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, taskOfResult);
            var local = new VariableDefinition(source);
            failLater.Body.Variables.Add(local);
            failLater.Body.InitLocals = true;
            {
                ILProcessor il = failLater.Body.GetILProcessor();
                Instruction done = il.Create(OpCodes.Ldloc, local);
                il.Emit(OpCodes.Newobj, sourceCtor);
                il.Emit(OpCodes.Stloc, local);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ldftn, fail);
                il.Emit(OpCodes.Newobj, callbackCtor);
                il.Emit(OpCodes.Ldloc, local);
                il.Emit(OpCodes.Call, post);
                il.Emit(OpCodes.Brtrue, done);
                il.Emit(OpCodes.Ldloc, local);
                il.Emit(OpCodes.Call, fail);
                il.Append(done);
                il.Emit(OpCodes.Callvirt, sourceTask);
                il.Emit(OpCodes.Ret);
                failLater.Body.OptimizeMacros();
            }
            helper.Methods.Add(failLater);
            return helper;
        }
    }
}
