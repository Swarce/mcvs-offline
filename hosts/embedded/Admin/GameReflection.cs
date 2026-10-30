// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent (reflection on Gameloft.Engine and Game): compiled into the embedded DLL and the net8 tests.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Server.Core;

namespace McvsOffline.Embedded.Admin
{
    /// <summary>
    /// What the settings page needs from the running game, by reflection only (no reference to Gameloft.Engine or Game, as
    /// the watchdog; design §8.2):
    /// <list type="bullet">
    /// <item><c>Gameloft.Engine.GameApplication.Instance</c>, its <c>World</c> (the client World, <c>Worlds[0]</c>) and its
    /// <c>Context</c> (the GameSynchronizationContext the game thread drains every frame);</item>
    /// <item><c>World.Browser</c> → <c>BrowserComponent.IsInGameBrowserInitialized</c> and <c>OpenUrl(string, bool)</c>;
    /// <c>Component.World</c> and <c>World.IsServer</c> (<c>bool?</c>);</item>
    /// <item><c>World.Authentication.AnonymousCredential</c> (<c>Args.Credential ?? anonymous:&lt;native user name&gt;</c>,
    /// AuthenticationComponent.cs:213), a <c>Gameloft.OnlinePlatform.Credential</c> turned into its string by the struct's own
    /// implicit conversion; <c>World.Authentication.FedId</c>;</item>
    /// <item><c>World.EnumerateActiveComponents(Type, ComponentGroupOptions)</c> with <c>Game.PlayerProfileComponent</c>, its
    /// <c>FedId</c> and <c>ForceSaveProfile(CancellationToken)</c> (a <c>[Remote(Any, Any)]</c> RPC: on the client it sends the
    /// game's own request to the menu server, which saves the profile through the dump POST).</item>
    /// <item><c>Game.UISceneExtentions.GetUI(World)</c> (the client World's <c>Game.UIComponent</c>, a
    /// <c>Gameloft.Engine.MenuComponent</c>) and the engine's Flash interop the game itself uses
    /// (OvertimeGaugeUIComponent.cs:81-87, 142-143): <c>MenuComponent.FindClass(string, string)</c> returning an
    /// <c>ASClass</c>, <c>ASClass/ASCharacter.InvokeMethod(string, object[])</c> and <c>ASCharacter.GetMember(string)</c>
    /// returning an <c>ASValue</c>, <c>ASCharacter.SetMember(string, object)</c>, <c>ASCharacter.Name</c>,
    /// <c>ASValue.GetObject(Type)</c>, all three IDisposable (<see cref="AccountMenuLayout"/>).</item>
    /// </list>
    /// Instance members are looked up by name on the object's runtime type (cached), so the same code drives the real game
    /// and the tests' look-alikes; <see cref="Verify"/> checks every member against the real assemblies
    /// (RealGameReflectionTests). Everything that touches game objects must run on the game thread (<see cref="Post"/>);
    /// nothing here throws into the caller except where stated.
    /// </summary>
    internal sealed class GameReflection
    {
        // Full type names only: their assemblies are found among the loaded ones (LoadedTypes), never bound by name here.
        public const string GameApplicationTypeName = "Gameloft.Engine.GameApplication";
        public const string PlayerProfileTypeName = "Game.PlayerProfileComponent";
        public const string UiExtensionsTypeName = "Game.UISceneExtentions";

        private static readonly object cacheGate = new object();
        private static readonly Dictionary<Type, Dictionary<string, MemberInfo>> cache = new Dictionary<Type, Dictionary<string, MemberInfo>>();

        private readonly object bindGate = new object();
        private volatile PropertyInfo instanceProperty;
        private volatile PropertyInfo worldProperty;
        private volatile PropertyInfo contextProperty;
        private volatile Type playerProfileType;
        private volatile Type uiExtensionsType;
        private bool lazy;
        private volatile bool complete;

        /// <summary>
        /// The game's types in the running process, bound lazily from the assemblies that are already loaded (none in tests):
        /// what is loaded now binds here, the rest on a later use (<see cref="EnsureBound"/>). It never makes the runtime load an
        /// assembly. On PC this runs at boot, before the engine loads Game by its full name through the .exe.config codeBase
        /// (gameload, design §8.9); build 17's <c>Type.GetType("Game.PlayerProfileComponent, Game")</c> here was a partial-name
        /// bind that loaded the stock Game.dll from the game folder first, so the engine got that one back. On Android Game is
        /// loaded from the APK before the bootstrap, and Gameloft.Engine long before the page or the notification prompt use this.
        /// </summary>
        public static GameReflection Load()
        {
            var game = new GameReflection { lazy = true };
            game.EnsureBound();
            return game;
        }

        /// <summary>
        /// <see cref="Load"/>'s binding, from the loaded assemblies only: what is still unbound is looked up again on every use
        /// until GameApplication (with World and Context), PlayerProfileComponent and UISceneExtentions are all known; from then on
        /// it costs a field read. Every public member that needs a bound type calls it. Never throws.
        /// </summary>
        private void EnsureBound()
        {
            if (!lazy || complete) return;
            lock (bindGate)
            {
                if (!lazy || complete) return;
                try
                {
                    if (!IsBoundNow)
                    {
                        Type app = LoadedTypes.Find(LoadedTypes.EngineAssembly, GameApplicationTypeName);
                        if (app != null) BindApplication(app);
                    }
                    if (playerProfileType == null) playerProfileType = LoadedTypes.Find(LoadedTypes.GameAssembly, PlayerProfileTypeName);
                    if (uiExtensionsType == null) uiExtensionsType = LoadedTypes.Find(LoadedTypes.GameAssembly, UiExtensionsTypeName);
                    complete = IsBoundNow && worldProperty != null && playerProfileType != null && uiExtensionsType != null;
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>Resolves the static side (tests pass look-alikes); false when GameApplication cannot be used.
        /// uiExtensions: Game.UISceneExtentions (its static GetUI(World)), may be null. The instance then stays as given (not lazy).</summary>
        public bool Bind(Type gameApplication, Type playerProfileComponent, Type uiExtensions = null)
        {
            try
            {
                lock (bindGate) lazy = false;
                playerProfileType = playerProfileComponent;
                uiExtensionsType = uiExtensions;
                if (gameApplication == null) return false;
                BindApplication(gameApplication);
                return IsBoundNow;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>World and Context first, Instance last: a reader on another thread that sees Instance sees the others.</summary>
        private void BindApplication(Type gameApplication)
        {
            worldProperty = FindProperty(gameApplication, "World", false);
            contextProperty = FindProperty(gameApplication, "Context", false);
            instanceProperty = FindProperty(gameApplication, "Instance", true);
        }

        private bool IsBoundNow { get { return instanceProperty != null && contextProperty != null; } }

        /// <summary>GameApplication and its game-thread context are known (after <see cref="Load"/>: once Gameloft.Engine is loaded).</summary>
        public bool IsBound
        {
            get
            {
                EnsureBound();
                return IsBoundNow;
            }
        }

        /// <summary>Game.PlayerProfileComponent as bound so far, or null (tests).</summary>
        internal Type PlayerProfileComponent { get { return playerProfileType; } }

        /// <summary>Game.UISceneExtentions as bound so far, or null (tests).</summary>
        internal Type UiExtensions { get { return uiExtensionsType; } }

        /// <summary><see cref="RequestSave"/> can work: the client World and PlayerProfileComponent are known.</summary>
        public bool CanSave { get { return IsBound && worldProperty != null && playerProfileType != null; } }

        /// <summary>GameApplication.Instance, or null (not created yet, or unbound). Any thread.</summary>
        public object Application()
        {
            try
            {
                EnsureBound();
                PropertyInfo instance = instanceProperty;
                return instance == null ? null : instance.GetValue(null, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>GameApplication.Instance.World (the client World), or null. Game thread.</summary>
        public object ClientWorld()
        {
            try
            {
                object app = Application();
                return app == null || worldProperty == null ? null : worldProperty.GetValue(app, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Game.UISceneExtentions.GetUI(GameApplication.Instance.World): the client World's UIComponent (the Flash
        /// menus), or null. Game thread.</summary>
        public object ClientUi()
        {
            try
            {
                object world = ClientWorld();
                if (world == null || uiExtensionsType == null) return null;
                MethodInfo getUi = FindStaticMethod(uiExtensionsType, "GetUI", world.GetType());
                return getUi == null ? null : getUi.Invoke(null, new[] { world });
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the game thread (GameApplication.Context, drained once per frame). The callback never
        /// throws into the game: an exception is logged. False when there is no game thread to post to.
        /// </summary>
        public bool Post(Action action, Log log)
        {
            try
            {
                object app = Application();
                var context = app == null || contextProperty == null ? null : contextProperty.GetValue(app, null) as SynchronizationContext;
                if (context == null) return false;
                context.Post(state =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            if (log != null) log.Warn("admin: a game-thread callback failed", ex);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }, null);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Whether the client World plays its main-menu level now (<see cref="IsAtMainMenu"/>), read on the game thread within
        /// <paramref name="timeoutMs"/>; null when unbound, on timeout, or when the game thread does not run frames.
        /// </summary>
        public bool? AtMainMenu(int timeoutMs)
        {
            var done = new ManualResetEvent(false);
            bool? result = null;
            if (!Post(() =>
            {
                try
                {
                    result = IsAtMainMenu(ClientWorld());
                }
                finally
                {
                    done.Set();
                }
            }, null)) return null;
            // Not disposed: a callback that runs after the timeout still sets it.
            return done.WaitOne(timeoutMs < 0 ? 0 : timeoutMs, false) ? result : null;
        }

        /// <summary>True once the game thread has drained a work item posted now (it runs frames); false on timeout or unbound.</summary>
        public bool WaitForFrame(int timeoutMs)
        {
            var ran = new ManualResetEvent(false);
            if (!Post(() => ran.Set(), null)) return false;
            // Not disposed: a callback that runs after the timeout still sets it.
            return ran.WaitOne(timeoutMs < 0 ? 0 : timeoutMs, false);
        }

        /// <summary>
        /// Asks the game to save the player's profile: posts <c>ForceSaveProfile(CancellationToken.None)</c> on the client World's
        /// own PlayerProfileComponent (the one whose FedId is the World's, else the first). Faults of the returned task are logged,
        /// never thrown. True once posted.
        /// </summary>
        public bool RequestSave(Log log)
        {
            if (!CanSave) return false;
            return Post(() =>
            {
                object world = ClientWorld();
                if (world == null || IsServer(world) != false)
                {
                    log.Warn("admin: no client World to save from");
                    return;
                }
                object profile = FindPlayerProfile(world);
                if (profile == null)
                {
                    log.Warn("admin: the client World has no active PlayerProfileComponent; nothing to save");
                    return;
                }
                MethodInfo force = FindMethod(profile.GetType(), "ForceSaveProfile", typeof(CancellationToken));
                if (force == null)
                {
                    log.Warn("admin: PlayerProfileComponent.ForceSaveProfile(CancellationToken) not found");
                    return;
                }
                var task = force.Invoke(profile, new object[] { CancellationToken.None }) as Task;
                if (task != null)
                {
                    task.ContinueWith(t =>
                    {
                        if (t.IsFaulted) log.Warn("admin: the game's ForceSaveProfile failed", t.Exception);
                        else if (t.IsCanceled) log.Info("admin: the game's ForceSaveProfile was cancelled");
                    });
                }
                log.Info("admin: asked the game to save the profile (ForceSaveProfile)");
            }, log);
        }

        /// <summary>The World's own active PlayerProfileComponent (FedId equal to World.Authentication.FedId), else the first one, else null.</summary>
        public object FindPlayerProfile(object world)
        {
            EnsureBound();
            Type profileType = playerProfileType;
            if (world == null || profileType == null) return null;
            MethodInfo enumerate = FindEnumerate(world.GetType());
            if (enumerate == null) return null;
            Type optionsType = enumerate.GetParameters()[1].ParameterType;
            var components = enumerate.Invoke(world, new object[] { profileType, Enum.ToObject(optionsType, 0) }) as IEnumerable;
            if (components == null) return null;
            string me = null;
            try
            {
                me = CredentialText(Property(Property(world, "Authentication"), "FedId"));
            }
            catch (Exception)
            {
            }
            object first = null;
            foreach (object component in components)
            {
                if (component == null) continue;
                if (first == null) first = component;
                if (string.IsNullOrEmpty(me)) break;
                string fedId = null;
                try
                {
                    fedId = CredentialText(Property(component, "FedId"));
                }
                catch (Exception)
                {
                }
                if (string.Equals(fedId, me, StringComparison.Ordinal)) return component;
            }
            return first;
        }

        // ------------------------------------------------------------------ instance members by name (game thread)

        /// <summary>World.Browser (BrowserComponent), or null.</summary>
        public static object Browser(object world)
        {
            return Property(world, "Browser");
        }

        /// <summary>BrowserComponent.IsInGameBrowserInitialized (a native read), or null when unknown.</summary>
        public static bool? IsInGameBrowserInitialized(object browser)
        {
            object value = Property(browser, "IsInGameBrowserInitialized");
            if (value is bool) return (bool)value;
            return null;
        }

        /// <summary>BrowserComponent.OpenUrl(url, true) (returns early on server Worlds, mutes the game's sounds, opens the native browser). Throws what the game throws.</summary>
        public static bool OpenUrl(object browser, string url)
        {
            if (browser == null) return false;
            MethodInfo open = FindMethod(browser.GetType(), "OpenUrl", typeof(string), typeof(bool));
            if (open == null) return false;
            open.Invoke(browser, new object[] { url, true });
            return true;
        }

        /// <summary>Component.World of a component (BrowserComponent), or null.</summary>
        public static object WorldOf(object component)
        {
            return Property(component, "World");
        }

        /// <summary>World.IsServer (bool?): null when unknown or not yet decided.</summary>
        public static bool? IsServer(object world)
        {
            object value = Property(world, "IsServer");
            if (value is bool) return (bool)value;
            return null;
        }

        /// <summary>
        /// Game thread: the client World plays its main menu (<c>World.CurrentLevelPath == World.Client.MainMenuScene</c>, the test
        /// GameGameOptionsComponent.SelectMipmapToSkip makes); null when the World is a server, unknown, or has no level yet.
        /// </summary>
        public static bool? IsAtMainMenu(object world)
        {
            if (world == null || IsServer(world) != false) return null;
            string level = Property(world, "CurrentLevelPath") as string;
            string menu = Property(Property(world, "Client"), "MainMenuScene") as string;
            if (string.IsNullOrEmpty(level) || string.IsNullOrEmpty(menu)) return null;
            return string.Equals(level, menu, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>World.Authentication.AnonymousCredential as the string the game sends ("anonymous:&lt;name&gt;"), or null.</summary>
        public static string AnonymousCredential(object world)
        {
            string text = CredentialText(Property(Property(world, "Authentication"), "AnonymousCredential"));
            return string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>
        /// A Gameloft.OnlinePlatform.Credential (boxed struct) as a string: its implicit conversion to string (FormattedCredential),
        /// else the FormattedCredential property, else ToString(). A string is returned as it is; null stays null.
        /// </summary>
        public static string CredentialText(object credential)
        {
            if (credential == null) return null;
            var text = credential as string;
            if (text != null) return text;
            Type type = credential.GetType();
            MethodInfo implicitToString = FindConversion(type);
            if (implicitToString != null) return implicitToString.Invoke(null, new[] { credential }) as string;
            PropertyInfo formatted = FindProperty(type, "FormattedCredential", false);
            if (formatted != null) return formatted.GetValue(credential, null) as string;
            return credential.ToString();
        }

        /// <summary>A public instance property's value by name, or null when the target is null or has no such property.</summary>
        public static object Property(object target, string name)
        {
            if (target == null) return null;
            PropertyInfo property = FindProperty(target.GetType(), name, false);
            return property == null ? null : property.GetValue(target, null);
        }

        // ------------------------------------------------------------------ lookups (cached per type)

        /// <summary>A public property (static or instance) by name; the most derived one when a subclass hides it.</summary>
        public static PropertyInfo FindProperty(Type type, string name, bool isStatic)
        {
            return (PropertyInfo)Cached(type, (isStatic ? "sp:" : "ip:") + name, () =>
            {
                BindingFlags flags = BindingFlags.Public | (isStatic ? BindingFlags.Static | BindingFlags.FlattenHierarchy : BindingFlags.Instance);
                PropertyInfo best = null;
                foreach (PropertyInfo p in type.GetProperties(flags))
                {
                    if (p.Name != name || p.GetIndexParameters().Length != 0 || p.GetGetMethod() == null) continue;
                    if (best == null || best.DeclaringType.IsAssignableFrom(p.DeclaringType)) best = p;
                }
                return best;
            });
        }

        /// <summary>A public instance method by name and exact parameter types.</summary>
        public static MethodInfo FindMethod(Type type, string name, params Type[] parameters)
        {
            var key = new System.Text.StringBuilder("m:").Append(name);
            foreach (Type p in parameters) key.Append(',').Append(p.FullName);
            return (MethodInfo)Cached(type, key.ToString(), () => type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, parameters, null));
        }

        /// <summary>
        /// A public instance method by name and exact parameter types that is not a generic method definition (the engine's
        /// ASCharacter has both <c>InvokeMethod(string, object[])</c> and <c>InvokeMethod&lt;T&gt;(string, object[])</c>).
        /// </summary>
        public static MethodInfo FindPlainMethod(Type type, string name, params Type[] parameters)
        {
            var key = new System.Text.StringBuilder("pm:").Append(name);
            foreach (Type p in parameters) key.Append(',').Append(p.FullName);
            return (MethodInfo)Cached(type, key.ToString(), () =>
            {
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != name || m.IsGenericMethodDefinition) continue;
                    ParameterInfo[] p = m.GetParameters();
                    if (p.Length != parameters.Length) continue;
                    bool same = true;
                    for (int i = 0; i < p.Length && same; i++) same = p[i].ParameterType == parameters[i];
                    if (same) return m;
                }
                return null;
            });
        }

        /// <summary>A public static, non-generic one-parameter method by name whose parameter accepts <paramref name="argument"/>.</summary>
        public static MethodInfo FindStaticMethod(Type type, string name, Type argument)
        {
            if (argument == null) return null;
            return (MethodInfo)Cached(type, "sm:" + name + "," + argument.AssemblyQualifiedName, () =>
            {
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != name || m.IsGenericMethodDefinition) continue;
                    ParameterInfo[] p = m.GetParameters();
                    if (p.Length == 1 && p[0].ParameterType.IsAssignableFrom(argument)) return m;
                }
                return null;
            });
        }

        /// <summary>World.EnumerateActiveComponents(Type, &lt;enum&gt; options).</summary>
        public static MethodInfo FindEnumerate(Type worldType)
        {
            return (MethodInfo)Cached(worldType, "enumerate", () =>
            {
                foreach (MethodInfo m in worldType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "EnumerateActiveComponents" || m.IsGenericMethodDefinition) continue;
                    ParameterInfo[] p = m.GetParameters();
                    if (p.Length == 2 && p[0].ParameterType == typeof(Type) && p[1].ParameterType.IsEnum &&
                        typeof(IEnumerable).IsAssignableFrom(m.ReturnType))
                        return m;
                }
                return null;
            });
        }

        /// <summary>The struct's own "public static implicit operator string(T)".</summary>
        public static MethodInfo FindConversion(Type type)
        {
            return (MethodInfo)Cached(type, "op_Implicit:string", () =>
            {
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "op_Implicit" || m.ReturnType != typeof(string)) continue;
                    ParameterInfo[] p = m.GetParameters();
                    if (p.Length == 1 && p[0].ParameterType == type) return m;
                }
                return null;
            });
        }

        private static MemberInfo Cached(Type type, string key, Func<MemberInfo> resolve)
        {
            if (type == null) return null;
            Dictionary<string, MemberInfo> members;
            lock (cacheGate)
            {
                if (!cache.TryGetValue(type, out members))
                {
                    members = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
                    cache[type] = members;
                }
                MemberInfo found;
                if (members.TryGetValue(key, out found)) return found;
            }
            MemberInfo resolved = null;
            try
            {
                resolved = resolve();
            }
            catch (Exception)
            {
            }
            lock (cacheGate) members[key] = resolved;
            return resolved;
        }

#if !MCVS_EMBEDDED
        // ------------------------------------------------------------------ verification against the real assemblies (tests only)

        /// <summary>
        /// Checks every member this class uses with the lookups it uses, against the game's real types (RealGameReflectionTests:
        /// <paramref name="resolve"/> maps a full type name to the type of the player's Gameloft.Engine.dll / Game.dll /
        /// Gameloft.OnlinePlatform.dll). Returns the problems (empty: all good).
        /// </summary>
        public static List<string> Verify(Func<string, Type> resolve)
        {
            var problems = new List<string>();
            Type app = resolve("Gameloft.Engine.GameApplication");
            Type world = resolve("Gameloft.Engine.World");
            Type browser = resolve("Gameloft.Engine.BrowserComponent");
            Type component = resolve("Gameloft.Engine.Component");
            Type authentication = resolve("Gameloft.Engine.AuthenticationComponent");
            Type options = resolve("Gameloft.Engine.ComponentGroupOptions");
            Type credential = resolve("Gameloft.OnlinePlatform.Credential");
            Type profile = resolve("Game.PlayerProfileComponent");
            Type clientComponent = resolve("Gameloft.Engine.ClientComponent");
            Type gameClient = resolve("Game.GameClientComponent");
            Type uiExtensions = resolve("Game.UISceneExtentions");
            Type uiComponent = resolve("Game.UIComponent");
            Type menuComponent = resolve("Gameloft.Engine.MenuComponent");
            Type asClass = resolve("Gameloft.Engine.ASClass");
            Type asCharacter = resolve("Gameloft.Engine.ASCharacter");
            Type asValue = resolve("Gameloft.Engine.ASValue");
            foreach (var t in new[] { app, world, browser, component, authentication, options, credential, profile, clientComponent, gameClient,
                                      uiExtensions, uiComponent, menuComponent, asClass, asCharacter, asValue })
            {
                if (t == null) problems.Add("a game type is missing");
            }
            if (problems.Count > 0) return problems;

            ExpectProperty(problems, app, "Instance", true, app);
            ExpectProperty(problems, app, "World", false, world);
            ExpectProperty(problems, app, "Context", false, typeof(SynchronizationContext));
            ExpectProperty(problems, world, "Browser", false, browser);
            ExpectProperty(problems, world, "Authentication", false, authentication);
            ExpectProperty(problems, world, "IsServer", false, typeof(bool?));
            ExpectProperty(problems, browser, "World", false, world);
            ExpectProperty(problems, browser, "IsInGameBrowserInitialized", false, typeof(bool));
            ExpectProperty(problems, authentication, "AnonymousCredential", false, credential);
            ExpectProperty(problems, authentication, "FedId", false, credential);
            ExpectProperty(problems, profile, "FedId", false, credential);
            ExpectProperty(problems, world, "CurrentLevelPath", false, typeof(string));
            ExpectProperty(problems, world, "Client", false, clientComponent);
            ExpectProperty(problems, gameClient, "MainMenuScene", false, typeof(string));
            if (!clientComponent.IsAssignableFrom(gameClient)) problems.Add("GameClientComponent is not a ClientComponent");
            if (!component.IsAssignableFrom(browser)) problems.Add("BrowserComponent is not a Component");

            MethodInfo open = FindMethod(browser, "OpenUrl", typeof(string), typeof(bool));
            if (open == null || open.ReturnType != typeof(void)) problems.Add("BrowserComponent.OpenUrl(string, bool) : void");
            MethodInfo enumerate = FindEnumerate(world);
            if (enumerate == null || enumerate.GetParameters()[1].ParameterType != options)
                problems.Add("World.EnumerateActiveComponents(Type, ComponentGroupOptions)");
            else if (!enumerate.ReturnType.IsGenericType || enumerate.ReturnType.GetGenericArguments()[0] != component)
                problems.Add("World.EnumerateActiveComponents returns " + enumerate.ReturnType);
            else if (!Enum.IsDefined(options, 0) || Enum.ToObject(options, 0).ToString() != "None")
                problems.Add("ComponentGroupOptions 0 is not None");
            MethodInfo force = FindMethod(profile, "ForceSaveProfile", typeof(CancellationToken));
            if (force == null || force.ReturnType != typeof(Task)) problems.Add("PlayerProfileComponent.ForceSaveProfile(CancellationToken) : Task");
            if (!component.IsAssignableFrom(profile)) problems.Add("PlayerProfileComponent is not a Component");
            if (FindConversion(credential) == null) problems.Add("Credential has no implicit conversion to string");
            VerifyFlash(problems, world, uiExtensions, uiComponent, menuComponent, asClass, asCharacter, asValue);
            return problems;
        }

        /// <summary>The Flash interop <see cref="AccountMenuLayout"/> drives, with the lookups it uses (and <see cref="ClientUi"/>'s).</summary>
        private static void VerifyFlash(List<string> problems, Type world, Type uiExtensions, Type uiComponent, Type menuComponent,
                                        Type asClass, Type asCharacter, Type asValue)
        {
            MethodInfo getUi = FindStaticMethod(uiExtensions, "GetUI", world);
            if (getUi == null || getUi.ReturnType != uiComponent) problems.Add("UISceneExtentions.GetUI(World) : UIComponent");
            if (!menuComponent.IsAssignableFrom(uiComponent)) problems.Add("UIComponent is not a MenuComponent");
            MethodInfo findClass = FindPlainMethod(uiComponent, "FindClass", typeof(string), typeof(string));
            if (findClass == null || findClass.ReturnType != asClass) problems.Add("MenuComponent.FindClass(string, string) : ASClass");
            foreach (Type owner in new[] { asClass, asCharacter })
            {
                MethodInfo invoke = FindPlainMethod(owner, "InvokeMethod", typeof(string), typeof(object[]));
                if (invoke == null || invoke.ReturnType != asValue) problems.Add(owner.Name + ".InvokeMethod(string, object[]) : ASValue");
            }
            MethodInfo getMember = FindPlainMethod(asCharacter, "GetMember", typeof(string));
            if (getMember == null || getMember.ReturnType != asValue) problems.Add("ASCharacter.GetMember(string) : ASValue");
            MethodInfo setMember = FindPlainMethod(asCharacter, "SetMember", typeof(string), typeof(object));
            if (setMember == null || setMember.ReturnType != typeof(void)) problems.Add("ASCharacter.SetMember(string, object) : void");
            ExpectProperty(problems, asCharacter, "Name", false, typeof(string));
            MethodInfo getObject = FindPlainMethod(asValue, "GetObject", typeof(Type));
            if (getObject == null || getObject.ReturnType != typeof(object)) problems.Add("ASValue.GetObject(Type) : object");
            foreach (Type disposable in new[] { asClass, asCharacter, asValue })
            {
                if (!typeof(IDisposable).IsAssignableFrom(disposable)) problems.Add(disposable.Name + " is not IDisposable");
            }
        }

        private static void ExpectProperty(List<string> problems, Type type, string name, bool isStatic, Type expected)
        {
            PropertyInfo p = FindProperty(type, name, isStatic);
            if (p == null) problems.Add(type.FullName + "." + name + " not found");
            else if (!expected.IsAssignableFrom(p.PropertyType)) problems.Add(type.FullName + "." + name + " is " + p.PropertyType + ", not " + expected);
        }
#endif
    }
}
