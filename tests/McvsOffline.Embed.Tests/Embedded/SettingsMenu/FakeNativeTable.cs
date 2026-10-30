using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace McvsOffline.Embed.Tests.Embedded.SettingsMenu
{
    /// <summary>
    /// A look-alike of the engine's Flash native table (Gameloft.Engine.MenuComponent, AND-CS MenuComponent.cs:733-924, PC :715-905),
    /// with its real rules:
    /// <list type="bullet">
    /// <item><c>OverrideNativeFunction(name, del, removePreviousOverrides = true, replaceOnlyMatchingDelegate = false)</c>: one entry per
    /// name (a new FuncID when the name is new); (true, false) clears the list, (true, true) removes the one delegate whose
    /// <c>Target</c> is <c>del.Target</c> (<c>SingleOrDefault</c>: two such delegates throw), (false, _) only appends; then appends.</item>
    /// <item><c>RemoveNativeFunctionOverride(string)</c> drops the entry and every delegate; <c>RemoveNativeFunctionOverride(object)</c>
    /// removes that delegate from every entry and drops the entries left empty.</item>
    /// <item><see cref="Invoke"/> (OnInvokeDelegate): every delegate of a snapshot of the list runs, in order; the <b>last non-null</b>
    /// return wins (null: Flash gets nothing); the arguments are converted to each delegate's own parameter types as
    /// <c>ASValue.GetObjectArray</c> does (an AS Number reaches an <c>object</c> parameter as float, a Boolean as bool); a delegate that
    /// throws stops the dispatch with a <see cref="FakeMenuInvocationException"/> (the game logs Fatal and rethrows into Flash).</item>
    /// </list>
    /// <see cref="FakeFlashTree"/> (the UIComponent) derives from it, so a host finds FindClass and OverrideNativeFunction on one object.
    /// </summary>
    internal class FakeNativeTable
    {
        // Static in the game (MenuComponent.nextFuncID): ids are unique in the process.
        private static int nextFuncId = 1;

        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        private sealed class Entry
        {
            public string Name;
            public int FuncId;
            public readonly List<Delegate> Delegates = new List<Delegate>();
        }

        /// <summary>"name" for every <see cref="Invoke"/>, in call order (nested calls included).</summary>
        public List<string> Invoked { get; } = new List<string>();

        /// <summary>How deep <see cref="Invoke"/> is now, and the deepest it has been (a native called from inside a native: skeptic E3).</summary>
        public int Depth { get; private set; }

        public int MaxDepth { get; private set; }

        public object OverrideNativeFunction(string funcName, Delegate del, bool removePreviousOverrides = true, bool replaceOnlyMatchingDelegate = false)
        {
            Entry entry;
            if (!entries.TryGetValue(funcName, out entry))
            {
                entry = new Entry { Name = funcName, FuncId = nextFuncId++ };
                entries[funcName] = entry;
            }
            if (removePreviousOverrides)
            {
                if (!replaceOnlyMatchingDelegate)
                {
                    entry.Delegates.Clear();
                }
                else
                {
                    // SingleOrDefault(x => x.Target == del.Target): reference equality on object, null == null for static delegates.
                    Delegate match = entry.Delegates.SingleOrDefault(x => x.Target == del.Target);
                    if ((object)match != null) entry.Delegates.Remove(match);
                }
            }
            entry.Delegates.Add(del);
            return del;
        }

        public void RemoveNativeFunctionOverride(string funcName)
        {
            entries.Remove(funcName);
        }

        public void RemoveNativeFunctionOverride(object key)
        {
            var del = (Delegate)key;
            foreach (Entry entry in entries.Values.ToList())
            {
                entry.Delegates.Remove(del);
                if (entry.Delegates.Count == 0) entries.Remove(entry.Name);
            }
        }

        /// <summary>The delegates registered on <paramref name="name"/>, in dispatch order (empty when the name has no entry).</summary>
        public IReadOnlyList<Delegate> DelegatesOf(string name)
        {
            Entry entry;
            return entries.TryGetValue(name, out entry) ? entry.Delegates.ToList() : new List<Delegate>();
        }

        public bool IsRegistered(string name)
        {
            return entries.ContainsKey(name);
        }

        /// <summary>The entry's FuncID (a new one after the name was removed and registered again), or 0.</summary>
        public int FuncIdOf(string name)
        {
            Entry entry;
            return entries.TryGetValue(name, out entry) ? entry.FuncId : 0;
        }

        /// <summary>The delegates on <paramref name="name"/> whose Target is <paramref name="target"/>.</summary>
        public int CountWithTarget(string name, object target)
        {
            return DelegatesOf(name).Count(d => ReferenceEquals(d.Target, target));
        }

        /// <summary>
        /// Flash calls the native <paramref name="name"/> (OnInvokeDelegate): what Flash gets back, or null (IntPtr.Zero). A name with no
        /// entry returns null too; use <see cref="CallFromFlash"/> to get the AS stub's answer then.
        /// </summary>
        public object Invoke(string name, params object[] args)
        {
            Invoked.Add(name);
            Entry entry;
            if (!entries.TryGetValue(name, out entry)) return null;
            Depth++;
            MaxDepth = Math.Max(MaxDepth, Depth);
            try
            {
                object result = null;
                foreach (Delegate del in entry.Delegates.ToArray())
                {
                    object value;
                    try
                    {
                        value = del.DynamicInvoke(Convert(del.Method.GetParameters(), args ?? new object[0]));
                    }
                    catch (Exception ex)
                    {
                        Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                        throw new FakeMenuInvocationException(name, "Failed to invoke '" + name + "' : " + cause.Message, cause);
                    }
                    if (value != null) result = value;
                }
                return result;
            }
            finally
            {
                Depth--;
            }
        }

        /// <summary>
        /// <c>Native.X(args)</c> in the ActionScript: the C# answer when the name is registered (the last non-null one, which may be
        /// null), else the AS stub's own body (<paramref name="stub"/>; Native.as returns true for both CanChange* natives).
        /// </summary>
        public object CallFromFlash(string name, object stub, params object[] args)
        {
            if (!IsRegistered(name))
            {
                Invoked.Add(name + " (stub)");
                return stub;
            }
            return Invoke(name, args);
        }

        /// <summary>ASValue.GetObjectArray(parameter types) on the AS arguments: missing ones are the type's default.</summary>
        private static object[] Convert(ParameterInfo[] parameters, object[] args)
        {
            var converted = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                Type type = parameters[i].ParameterType;
                object arg = i < args.Length ? args[i] : null;
                converted[i] = ConvertArgument(arg, type);
            }
            return converted;
        }

        internal static object ConvertArgument(object arg, Type type)
        {
            if (type == typeof(object)) return IsNumber(arg) ? (object)System.Convert.ToSingle(arg, CultureInfo.InvariantCulture) : arg;
            if (arg == null) return type.IsValueType ? Activator.CreateInstance(type) : null;
            if (type == typeof(int)) return arg is bool ? ((bool)arg ? 1 : 0) : System.Convert.ToInt32(arg, CultureInfo.InvariantCulture);
            if (type == typeof(float)) return System.Convert.ToSingle(arg, CultureInfo.InvariantCulture);
            if (type == typeof(bool)) return arg is bool ? arg : (object)(System.Convert.ToDouble(arg, CultureInfo.InvariantCulture) != 0);
            if (type == typeof(string)) return arg as string ?? System.Convert.ToString(arg, CultureInfo.InvariantCulture);
            return arg;
        }

        internal static bool IsNumber(object value)
        {
            return value is int || value is long || value is float || value is double || value is uint || value is short;
        }
    }

    /// <summary>Gameloft.Engine.MenuInvokationException: what reaches Flash when a native's delegate throws.</summary>
    internal sealed class FakeMenuInvocationException : Exception
    {
        public FakeMenuInvocationException(string native, string message, Exception inner) : base(message, inner)
        {
            Native = native;
        }

        public string Native { get; private set; }
    }

    /// <summary>Game.PopupPriority (ShowGenericConfirmPopup's last parameter).</summary>
    internal enum FakePopupPriority
    {
        OnTop = 0,
        AtEnd = 1,
    }

    /// <summary>
    /// The game's own delegates on the natives the Settings menu code touches, with the game's behaviour (Android 1.17.32):
    /// <list type="bullet">
    /// <item>UIComponent.OnStart (:402, :1449, :1455): GoToForum, EnterSection, GetPlatformId; OnStop (:416, :1578) removes them by name.</item>
    /// <item>GameClientComponent (:3815-3816): CanChangeMaxFramerate (GameOptions <c>canChangeMaxFramerate</c>, true on Android) and
    /// GetMaxFramerateValues (<c>GamePreferencesComponent.FpsValues</c>, "30 60 -1"); removed at :3878-3879.</item>
    /// <item>GamePreferencesComponent.OnStart (:146-151): Set/Get/SaveUserPreferences, ChangeGraphicSetting, CanChangeGraphicSetting
    /// (true only in GameMode.MENU); OnStop (:167-173) removes the three preference natives (not the two graphics ones).</item>
    /// <item>Preferences as UserPreferencesComponent keeps them (:111-149): an unknown key reads null and is not written; a numeric
    /// value is converted to a numeric property, any other type mismatch is ignored.</item>
    /// </list>
    /// </summary>
    internal sealed class FakeGameNatives
    {
        /// <summary>The [UserPreference] properties of Android's GamePreferencesComponent (GamePreferencesComponent.cs:18-90).</summary>
        public static readonly Dictionary<string, Type> PreferenceTypes = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            { "CameraSensitivity", typeof(float) }, { "CameraSmoothing", typeof(float) }, { "InvertCameraX", typeof(bool) },
            { "InvertCameraY", typeof(bool) }, { "SprintSensitivity", typeof(float) }, { "EnableAutoShoot", typeof(bool) },
            { "AutoShootAlwaysActive", typeof(bool) }, { "IronSightButton", typeof(bool) }, { "IronSightMode", typeof(int) },
            { "AimBehavior", typeof(int) }, { "AbilityButtonPlacement", typeof(int) }, { "SingleTapExitIronSight", typeof(bool) },
            { "EnableAimAssist", typeof(bool) }, { "DoubleTapHoldToIronSightFire", typeof(bool) }, { "AutoShootCancelsSprinting", typeof(bool) },
            { "SprintCancelsReload", typeof(bool) }, { "AutoShootSwitcher", typeof(bool) }, { "ReturnToIronSightAfterReload", typeof(bool) },
            { "CustomizedHud", typeof(string) }, { "EnableNotifications", typeof(bool) }, { "PlayerFov", typeof(int) },
            { "ControlScheme", typeof(int) }, { "MaxFramerate", typeof(int) }, { "GraphicSetting", typeof(int) },
        };

        public FakeGameNatives()
        {
            foreach (KeyValuePair<string, Type> kv in PreferenceTypes)
            {
                Preferences[kv.Key] = kv.Value == typeof(string) ? (object)"" : Activator.CreateInstance(kv.Value);
            }
            // The game's own initialisers (PlayerFov = 80, GraphicSetting = 1); the slider values are guesses [G].
            Preferences["PlayerFov"] = 80;
            Preferences["GraphicSetting"] = 1;
            Preferences["CameraSensitivity"] = 0.5f;
            Preferences["CameraSmoothing"] = 0.5f;
            Preferences["SprintSensitivity"] = 0.5f;
        }

        /// <summary>The preference values (property name → int, float, bool or string).</summary>
        public Dictionary<string, object> Preferences { get; } = new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>GamePreferencesComponent.FpsValues as GameOptions "30 60 -1" resolves it.</summary>
        public List<int> FpsValues { get; set; } = new List<int> { 30, 60, -1 };

        /// <summary>GameClientComponent.CanChangeMaxFramerate (GameOptions_android.json:236-237).</summary>
        public bool CanChangeMaxFramerateValue { get; set; } = true;

        /// <summary>GameplayComponent.GameMode == MENU (the CanChangeGraphicSetting gate, GamePreferencesComponent.cs:92-103).</summary>
        public bool InMenu { get; set; } = true;

        /// <summary>GameApplication.Instance.PlatformId (Android 2, Steam 6).</summary>
        public int PlatformId { get; set; } = 2;

        /// <summary>The game's GoToForum body ran (UIComponent.GoToForum → Browser.ShowForum, or patch 'forum').</summary>
        public int ForumCalls { get; set; }

        /// <summary>Sections the game's EnterSection delegate got.</summary>
        public List<string> Sections { get; } = new List<string>();

        /// <summary>Accepted writes, "key=value" in order (rejected ones are not listed).</summary>
        public List<string> Writes { get; } = new List<string>();

        public int Saves { get; private set; }

        /// <summary>ChangeGraphicSetting values (AppPreferences GameOptions_SelectedProfile = value − 1, then the restart prompt).</summary>
        public List<int> GraphicSettingChanges { get; } = new List<int>();

        // ------------------------------------------------------------------ registration as the game's components do it

        /// <summary>UIComponent.OnStart: GoToForum (target: the UIComponent itself), EnterSection, GetPlatformId; default flags (clear).</summary>
        public void RegisterUi(FakeFlashTree ui)
        {
            ui.OverrideNativeFunction("GoToForum", new Action(ui.GoToForum));
            ui.OverrideNativeFunction("EnterSection", new Action<string>(NativeEnterSection));
            ui.OverrideNativeFunction("GetPlatformId", new Func<int>(GetPlatformId));
        }

        public void StopUi(FakeNativeTable ui)
        {
            ui.RemoveNativeFunctionOverride("GoToForum");
            ui.RemoveNativeFunctionOverride("EnterSection");
            ui.RemoveNativeFunctionOverride("GetPlatformId");
        }

        /// <summary>GameClientComponent: CanChangeMaxFramerate and GetMaxFramerateValues (lambdas, as the game registers them).</summary>
        public void RegisterClient(FakeNativeTable ui)
        {
            ui.OverrideNativeFunction("CanChangeMaxFramerate", (Func<bool>)(() => CanChangeMaxFramerateValue));
            ui.OverrideNativeFunction("GetMaxFramerateValues", (Func<int[]>)(() => FpsValues.ToArray()));
        }

        public void StopClient(FakeNativeTable ui)
        {
            ui.RemoveNativeFunctionOverride("CanChangeMaxFramerate");
            ui.RemoveNativeFunctionOverride("GetMaxFramerateValues");
        }

        /// <summary>GamePreferencesComponent.OnStart.</summary>
        public void RegisterPreferences(FakeNativeTable ui)
        {
            ui.OverrideNativeFunction("SetUserPreference", new Action<string, object>(NativeSetUserPreference));
            ui.OverrideNativeFunction("GetUserPreference", new Func<string, object>(NativeGetUserPreference));
            ui.OverrideNativeFunction("SaveUserPreferences", new Action(NativeSaveUserPreferences));
            ui.OverrideNativeFunction("ChangeGraphicSetting", new Action<int>(NativeChangeGraphicSetting));
            ui.OverrideNativeFunction("CanChangeGraphicSetting", (Func<bool>)(() => InMenu));
        }

        /// <summary>GamePreferencesComponent.OnStop: the three preference natives only.</summary>
        public void StopPreferences(FakeNativeTable ui)
        {
            ui.RemoveNativeFunctionOverride("SetUserPreference");
            ui.RemoveNativeFunctionOverride("GetUserPreference");
            ui.RemoveNativeFunctionOverride("SaveUserPreferences");
        }

        /// <summary>Every component's OnStart, in the game's order.</summary>
        public void RegisterAll(FakeFlashTree ui)
        {
            RegisterUi(ui);
            RegisterClient(ui);
            RegisterPreferences(ui);
        }

        // ------------------------------------------------------------------ the bodies

        public void NativeEnterSection(string section)
        {
            Sections.Add(section);
        }

        public int GetPlatformId()
        {
            return PlatformId;
        }

        public void NativeSetUserPreference(string propertyName, object value)
        {
            Type type;
            if (propertyName == null || !PreferenceTypes.TryGetValue(propertyName, out type)) return;
            if (value == null) throw new NullReferenceException("value.GetType()");   // the game's own NRE (UserPreferencesComponent.cs:123)
            if (IsNumeric(type) && FakeNativeTable.IsNumber(value)) value = System.Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            else if (type != value.GetType()) return;
            Preferences[propertyName] = value;
            Writes.Add(propertyName + "=" + System.Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        public object NativeGetUserPreference(string propertyName)
        {
            object value;
            return propertyName != null && Preferences.TryGetValue(propertyName, out value) ? value : null;
        }

        public void NativeSaveUserPreferences()
        {
            Saves++;
        }

        public void NativeChangeGraphicSetting(int value)
        {
            GraphicSettingChanges.Add(value);
        }

        private static bool IsNumeric(Type type)
        {
            return type == typeof(int) || type == typeof(float) || type == typeof(double) || type == typeof(long);
        }
    }
}
