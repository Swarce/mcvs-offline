// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent (reflection on Gameloft.Engine and Game): compiled into the embedded DLL and the net8 tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace McvsOffline.Embedded.Admin.SettingsMenu
{
    /// <summary>
    /// The engine's Flash interop and native table by name, for the Settings menu code (docs/design/settings-menu.md §3.1). A copy
    /// of AccountMenuLayout's private helpers (AccountMenuLayout.cs:233-300; the two are merged in a later cleanup), plus the
    /// MenuComponent native table members and <see cref="FlashScope"/>:
    /// <list type="bullet">
    /// <item><c>MenuComponent.FindClass(string, string)</c> → <c>ASClass</c>; <c>ASClass/ASCharacter.InvokeMethod(string, object[])</c>
    /// and <c>ASCharacter.GetMember(string)</c> → <c>ASValue</c>; <c>ASCharacter.SetMember(string, object)</c>;
    /// <c>ASValue.GetObject(Type)</c> by the value's own type (Number → float, Character → ASCharacter, Null → null). All three
    /// wrappers are IDisposable.</item>
    /// <item><c>MenuComponent.OverrideNativeFunction(string, Delegate, bool, bool)</c> and
    /// <c>RemoveNativeFunctionOverride(string)</c> (MenuComponent.cs:733-747): the game's own registry. Dispatch calls every
    /// delegate in list order and the last non-null return wins (:869-924); a delegate that throws is rethrown into Flash, so
    /// every delegate of ours catches everything.</item>
    /// <item><c>UIComponent.GoToForum()</c> (UIComponent.cs:361) and <c>ShowGenericConfirmPopup(string, Dictionary&lt;string, object&gt;,
    /// Action&lt;Dictionary&lt;string, object&gt;&gt;, PopupPriority)</c> (:1854).</item>
    /// </list>
    /// Members are found on the object's runtime type (<see cref="GameReflection.FindPlainMethod"/>), so the same code drives the
    /// game and the tests' look-alikes. Game thread only. The helpers throw <see cref="MissingMethodException"/> for a missing
    /// member and let the member's own exception through; their callers catch.
    /// </summary>
    internal static class FlashInterop
    {
        /// <summary>MenuComponent.FindClass(package, class): an ASClass, or null.</summary>
        public static object FindClass(object ui, string package, string className)
        {
            MethodInfo find = GameReflection.FindPlainMethod(ui.GetType(), "FindClass", typeof(string), typeof(string));
            if (find == null) throw new MissingMethodException(ui.GetType().Name, "FindClass");
            return find.Invoke(ui, new object[] { package, className });
        }

        /// <summary>ASClass/ASCharacter.InvokeMethod(name, args): an ASValue, or null.</summary>
        public static object Invoke(object target, string method, params object[] args)
        {
            MethodInfo invoke = GameReflection.FindPlainMethod(target.GetType(), "InvokeMethod", typeof(string), typeof(object[]));
            if (invoke == null) throw new MissingMethodException(target.GetType().Name, "InvokeMethod");
            return invoke.Invoke(target, new object[] { method, args ?? new object[0] });
        }

        /// <summary>ASCharacter.GetMember(name): an ASValue, or null.</summary>
        public static object GetMember(object target, string member)
        {
            MethodInfo get = GameReflection.FindPlainMethod(target.GetType(), "GetMember", typeof(string));
            if (get == null) throw new MissingMethodException(target.GetType().Name, "GetMember");
            return get.Invoke(target, new object[] { member });
        }

        /// <summary>ASCharacter.SetMember(name, value). Numbers go as float (ASValue.SetObject: a double would become a managed object).</summary>
        public static void SetMember(object target, string member, object value)
        {
            MethodInfo set = GameReflection.FindPlainMethod(target.GetType(), "SetMember", typeof(string), typeof(object));
            if (set == null) throw new MissingMethodException(target.GetType().Name, "SetMember");
            set.Invoke(target, new[] { member, value });
        }

        /// <summary>
        /// ASValue.GetObject(null): by the value's own type (Number → float, Character → ASCharacter, Null → null). The ASValue is
        /// disposed (kept in <paramref name="owned"/> until the end).
        /// </summary>
        public static object Value(List<object> owned, object asValue)
        {
            if (asValue == null) return null;
            Own(owned, asValue);
            MethodInfo get = GameReflection.FindPlainMethod(asValue.GetType(), "GetObject", typeof(Type));
            if (get == null) throw new MissingMethodException(asValue.GetType().Name, "GetObject");
            return get.Invoke(asValue, new object[] { null });
        }

        /// <summary>An AS Number as a double (it arrives as float), or null for anything else.</summary>
        public static double? Number(object value)
        {
            if (value is float) return (float)value;
            if (value is double) return (double)value;
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            if (value is uint) return (uint)value;
            return null;
        }

        /// <summary>Keeps <paramref name="wrapper"/> for disposal at the end (once: by reference, as ASCharacter's Equals compares the native character).</summary>
        public static object Own(List<object> owned, object wrapper)
        {
            if (wrapper == null) return null;
            foreach (object o in owned)
            {
                if (ReferenceEquals(o, wrapper)) return wrapper;
            }
            owned.Add(wrapper);
            return wrapper;
        }

        /// <summary>Disposes every wrapper in <paramref name="owned"/>, last first; never throws.</summary>
        public static void DisposeAll(List<object> owned)
        {
            if (owned == null) return;
            for (int i = owned.Count - 1; i >= 0; i--)
            {
                try
                {
                    var disposable = owned[i] as IDisposable;
                    if (disposable != null) disposable.Dispose();
                }
                catch (Exception)
                {
                }
            }
            owned.Clear();
        }

        // ------------------------------------------------------------------ the native table (MenuComponent)

        /// <summary>
        /// MenuComponent.OverrideNativeFunction(name, del, removePreviousOverrides, replaceOnlyMatchingDelegate). With (true, true) it
        /// removes only the delegate whose <c>Target</c> is <paramref name="del"/>'s and appends <paramref name="del"/> (it throws when
        /// two have that Target: give every name its own target object); (true, false) replaces the whole list; (false, _) appends.
        /// </summary>
        public static void OverrideNative(object ui, string name, Delegate del, bool removePrevious, bool replaceOnlyMatching)
        {
            MethodInfo method = GameReflection.FindPlainMethod(ui.GetType(), "OverrideNativeFunction", typeof(string), typeof(Delegate), typeof(bool), typeof(bool));
            if (method == null) throw new MissingMethodException(ui.GetType().Name, "OverrideNativeFunction");
            method.Invoke(ui, new object[] { name, del, removePrevious, replaceOnlyMatching });
        }

        /// <summary>MenuComponent.RemoveNativeFunctionOverride(string): drops the name and every delegate on it (the game's own too).</summary>
        public static void RemoveNative(object ui, string name)
        {
            MethodInfo method = GameReflection.FindPlainMethod(ui.GetType(), "RemoveNativeFunctionOverride", typeof(string));
            if (method == null) throw new MissingMethodException(ui.GetType().Name, "RemoveNativeFunctionOverride");
            method.Invoke(ui, new object[] { name });
        }

        /// <summary>UIComponent.GoToForum() itself (the stock body, or patch 'forum''s; skeptic E4).</summary>
        public static void GoToForum(object ui)
        {
            MethodInfo method = GameReflection.FindPlainMethod(ui.GetType(), "GoToForum");
            if (method == null) throw new MissingMethodException(ui.GetType().Name, "GoToForum");
            method.Invoke(ui, new object[0]);
        }

        /// <summary>
        /// UIComponent.ShowGenericConfirmPopup(eventName, data, callback, priority) with the default priority (PopupPriority.AtEnd,
        /// the parameter's own default) and a callback that gets the popup's answer (<c>r["result"]</c> true for Yes; GamePreferencesComponent.cs:220-226).
        /// </summary>
        public static void ShowConfirmPopup(object ui, string eventName, Dictionary<string, object> data, Action<Dictionary<string, object>> answered)
        {
            MethodInfo method = null;
            foreach (MethodInfo m in ui.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "ShowGenericConfirmPopup" || m.IsGenericMethodDefinition) continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length == 4 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(Dictionary<string, object>) &&
                    p[2].ParameterType == typeof(Action<Dictionary<string, object>>) && p[3].ParameterType.IsEnum && p[3].IsOptional)
                {
                    method = m;
                    break;
                }
            }
            if (method == null) throw new MissingMethodException(ui.GetType().Name, "ShowGenericConfirmPopup");
            ParameterInfo last = method.GetParameters()[3];
            object fallback = last.DefaultValue;
            long number = fallback is Enum || fallback is int || fallback is long ? Convert.ToInt64(fallback, CultureInfo.InvariantCulture) : 0;
            object priority = Enum.ToObject(last.ParameterType, number);
            method.Invoke(ui, new[] { eventName, data, answered, priority });
        }

        /// <summary>"0.##" in the invariant culture (log lines).</summary>
        public static string Text(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The wrappers one game-thread step got from the engine (ASClass, ASCharacter, ASValue), disposed together at the end:
    /// <c>using (var flash = new FlashScope(ui)) { ... }</c>. Reads give plain values; every handle stays valid until Dispose.
    /// </summary>
    internal sealed class FlashScope : IDisposable
    {
        private readonly List<object> owned = new List<object>();

        public FlashScope(object ui)
        {
            Ui = ui;
        }

        /// <summary>The UIComponent the step works on.</summary>
        public object Ui { get; private set; }

        /// <summary>The wrappers kept so far (tests).</summary>
        internal int Count { get { return owned.Count; } }

        /// <summary>FindClass(package, class) on <see cref="Ui"/>, or null.</summary>
        public object Class(string package, string className)
        {
            return FlashInterop.Own(owned, FlashInterop.FindClass(Ui, package, className));
        }

        /// <summary>target.InvokeMethod(method, args) read as a plain value (an ASCharacter is kept).</summary>
        public object Call(object target, string method, params object[] args)
        {
            return Keep(FlashInterop.Value(owned, FlashInterop.Invoke(target, method, args)));
        }

        /// <summary>target.GetMember(member) read as a plain value (an ASCharacter is kept).</summary>
        public object Get(object target, string member)
        {
            return Keep(FlashInterop.Value(owned, FlashInterop.GetMember(target, member)));
        }

        /// <summary>A number member as a double, or null.</summary>
        public double? Number(object target, string member)
        {
            return FlashInterop.Number(Get(target, member));
        }

        /// <summary>A child by name (<c>getChildByName</c>), or null.</summary>
        public object Child(object parent, string name)
        {
            return Call(parent, "getChildByName", name);
        }

        public void Set(object target, string member, object value)
        {
            FlashInterop.SetMember(target, member, value);
        }

        private object Keep(object value)
        {
            if (value == null || value is string || value is float || value is bool || value is double || value is int) return value;
            return FlashInterop.Own(owned, value);
        }

        public void Dispose()
        {
            FlashInterop.DisposeAll(owned);
        }
    }
}
