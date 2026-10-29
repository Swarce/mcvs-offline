// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent (reflection on Gameloft.Engine and Game): compiled into the embedded DLL and the net8 tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using McvsOffline.Server.Core;

namespace McvsOffline.Embedded.Admin
{
    /// <summary>
    /// Settings › Account with <c>admin.accountMenu.hideDeadButtons</c> on (design §9.2): which of the game's entries
    /// <see cref="AdminHooks.AccountMenuButtons"/> drops, and the step that centres the one or two buttons left.
    /// <list type="bullet">
    /// <item>Dropped: <c>snsId</c> 0 (Facebook) and 2 (Google Play Games), whose login can never succeed offline (patch
    /// <c>debloat</c>), and <c>customAction "restore_purchase"</c>, the in-app-purchase restore (no store exists offline).
    /// Every other entry stays (Redeem code, the PC's GameCenter entry, the link-code entry of other platforms, anything
    /// unknown), and so does an entry whose members cannot be read. The Steam build's Win32 list is [Redeem, GameCenter],
    /// so nothing is dropped or centred on PC.</item>
    /// <item>Centring: Flash (<c>MenuAccountSettings.onPush</c>) puts list key k at <c>x = 77 + floor(k/2)·(w+50)</c>,
    /// <c>y = 320 + (k%2)·(h+45)</c>, so [Redeem, MCVS Offline] would be stacked at the left edge. After the hook returns,
    /// one work item on the game thread (it runs after <c>onPush</c>, when the buttons exist) finds the menu on top of
    /// <c>MenusStack</c> (named <c>menu_accounts</c>), its <c>btnSocial_customAction0/1</c> children and the viewport
    /// (<c>VPManager.getViewportBoundX/Width</c>), and puts the buttons side by side in the middle of the viewport, 50 apart,
    /// on the first row. Any failure logs one line and leaves the game's layout.</item>
    /// </list>
    /// Everything goes through the engine's own Flash interop (<c>MenuComponent.FindClass</c>, <c>ASClass</c>/<c>ASCharacter</c>
    /// <c>InvokeMethod</c>/<c>GetMember</c>/<c>SetMember</c>, <c>ASValue.GetObject</c>) by reflection; <see cref="GameReflection.Verify"/>
    /// checks those members against the real assemblies.
    /// </summary>
    internal static class AccountMenuLayout
    {
        public const int SnsFacebook = 0;
        public const int SnsGooglePlayGames = 2;
        public const string RestorePurchase = "restore_purchase";

        public const string MenuName = "menu_accounts";
        public const string ButtonPrefix = "btnSocial_customAction";

        /// <summary>The game's column gap (MenuAccountSettings.as:208, stage units).</summary>
        public const double Gap = 50;

        /// <summary>NaN (the trimmed BCL of the game has no double.NaN field).</summary>
        private const double NotSet = 0.0 / 0.0;

        // ------------------------------------------------------------------ the filter (any thread)

        /// <summary>Facebook, Google Play Games or the purchase restore; false for anything else and on any read error.</summary>
        public static bool IsDeadEntry(object entry)
        {
            try
            {
                if (entry == null) return false;
                long sns;
                if (TryInteger(Member(entry, "snsId"), out sns) && (sns == SnsFacebook || sns == SnsGooglePlayGames)) return true;
                return string.Equals(Member(entry, "customAction") as string, RestorePurchase, StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The final list gets its own buttons named <c>btnSocial_customAction0..n-1</c> and can be centred: 1 or 2 entries, none
        /// with an <c>snsId</c> (Flash names those <c>btnSocial_&lt;snsId&gt;</c>). False on any read error.
        /// </summary>
        public static bool CanCentre(IList<object> entries)
        {
            try
            {
                if (entries == null || entries.Count < 1 || entries.Count > 2) return false;
                foreach (object entry in entries)
                {
                    if (entry == null || Member(entry, "snsId") != null) return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>A member of one of the game's anonymous entries (a public property) or of our own dictionary entry; null when absent.</summary>
        internal static object Member(object entry, string name)
        {
            var dictionary = entry as IDictionary<string, object>;
            if (dictionary != null)
            {
                object value;
                return dictionary.TryGetValue(name, out value) ? value : null;
            }
            return GameReflection.Property(entry, name);
        }

        private static bool TryInteger(object value, out long result)
        {
            result = 0;
            if (value == null) return false;
            if (value is int) result = (int)value;
            else if (value is long) result = (long)value;
            else if (value is short) result = (short)value;
            else if (value is Enum) result = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            else return false;
            return true;
        }

        // ------------------------------------------------------------------ the layout

        /// <summary>
        /// Where the buttons go: side by side in the middle of the viewport [vpX, vpX+vpW], <see cref="Gap"/> apart. One button
        /// (w1 null): x1 is NaN. False when a value is not a finite positive size or the row does not fit.
        /// S22 (layout.verify.md §2): vpX 0, vpW 2048, w 841 → x0 158, x1 1049; one button → 603.5.
        /// </summary>
        public static bool Plan(double vpX, double vpW, double w0, double? w1, out double x0, out double x1)
        {
            x0 = NotSet;
            x1 = NotSet;
            if (!Finite(vpX) || !Finite(vpW) || vpW <= 0 || !Finite(w0) || w0 <= 0) return false;
            if (w1.HasValue && (!Finite(w1.Value) || w1.Value <= 0)) return false;
            double total = w1.HasValue ? w0 + Gap + w1.Value : w0;
            if (total > vpW) return false;
            x0 = vpX + (vpW - total) / 2;
            if (w1.HasValue) x1 = x0 + w0 + Gap;
            return true;
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>Queues <see cref="Centre"/> for <paramref name="count"/> buttons on the game thread; one log line when it cannot.</summary>
        public static void Schedule(GameReflection game, Log log, int count)
        {
            try
            {
                if (game == null || !game.Post(() => Centre(game, log, count), log)) Write(log, "account menu: not centred (no game thread)");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Game thread, after MenuAccountSettings.onPush. Logs exactly one line; never throws.</summary>
        public static void Centre(GameReflection game, Log log, int count)
        {
            var owned = new List<object>();
            try
            {
                string problem = TryCentre(game, count, owned, out string done);
                Write(log, problem == null ? done : "account menu: not centred (" + problem + ")");
            }
            catch (Exception ex)
            {
                Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Write(log, "account menu: not centred (" + cause.GetType().Name + ": " + cause.Message + ")");
            }
            finally
            {
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
            }
        }

        /// <summary>Null when centred (<paramref name="done"/> is the log line), else why not. Reads everything before it moves anything.</summary>
        private static string TryCentre(GameReflection game, int count, List<object> owned, out string done)
        {
            done = null;
            if (count < 1 || count > 2) return count + " entries";
            object ui = game == null ? null : game.ClientUi();
            if (ui == null) return "no UI";

            object stack = Own(owned, FindClass(ui, "gluic.components.menus", "MenusStack"));
            if (stack == null) return "no MenusStack";
            object menu = Own(owned, Value(owned, Invoke(stack, "getMenuOnTopOfStack")));
            if (menu == null) return "no menu on top";
            string name = GameReflection.Property(menu, "Name") as string;
            if (name != MenuName)
            {
                string asName = Value(owned, GetMember(menu, "name")) as string;
                if (asName != null) name = asName;
            }
            if (name != MenuName) return "the menu on top is " + (name ?? "unnamed");

            var buttons = new object[count];
            var widths = new double[count];
            for (int i = 0; i < count; i++)
            {
                buttons[i] = Own(owned, Value(owned, Invoke(menu, "getChildByName", ButtonPrefix + i.ToString(CultureInfo.InvariantCulture))));
                if (buttons[i] == null) return "no " + ButtonPrefix + i.ToString(CultureInfo.InvariantCulture);
                double? width = Number(Value(owned, GetMember(buttons[i], "width")));
                if (width == null) return ButtonPrefix + i.ToString(CultureInfo.InvariantCulture) + " has no width";
                widths[i] = width.Value;
            }
            double? y0 = Number(Value(owned, GetMember(buttons[0], "y")));
            if (y0 == null) return ButtonPrefix + "0 has no y";

            object viewport = Own(owned, FindClass(ui, "managers", "VPManager"));
            if (viewport == null) return "no VPManager";
            double? vpX = Number(Value(owned, Invoke(viewport, "getViewportBoundX")));
            double? vpW = Number(Value(owned, Invoke(viewport, "getViewportBoundWidth")));
            if (vpX == null || vpW == null) return "no viewport bounds";

            double x0, x1;
            if (!Plan(vpX.Value, vpW.Value, widths[0], count == 2 ? widths[1] : (double?)null, out x0, out x1))
                return "does not fit (vpX=" + Text(vpX.Value) + ", vpW=" + Text(vpW.Value) + ", w0=" + Text(widths[0]) +
                       (count == 2 ? ", w1=" + Text(widths[1]) : "") + ")";

            // ASValue.SetObject turns a float into an AS Number; a double would become an opaque managed object.
            SetMember(buttons[0], "x", (float)x0);
            if (count == 2)
            {
                SetMember(buttons[1], "x", (float)x1);
                SetMember(buttons[1], "y", (float)y0.Value);
            }
            done = "account menu: " + count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " button" : " buttons") +
                   " centred (vpX=" + Text(vpX.Value) + ", vpW=" + Text(vpW.Value) + ", x0=" + Text(x0) +
                   (count == 2 ? ", x1=" + Text(x1) : "") + ")";
            return null;
        }

        // ------------------------------------------------------------------ the engine's Flash interop, by name

        /// <summary>MenuComponent.FindClass(package, class): an ASClass, or null.</summary>
        private static object FindClass(object ui, string package, string className)
        {
            MethodInfo find = GameReflection.FindPlainMethod(ui.GetType(), "FindClass", typeof(string), typeof(string));
            if (find == null) throw new MissingMethodException(ui.GetType().Name, "FindClass");
            return find.Invoke(ui, new object[] { package, className });
        }

        /// <summary>ASClass/ASCharacter.InvokeMethod(name, args): an ASValue, or null.</summary>
        private static object Invoke(object target, string method, params object[] args)
        {
            MethodInfo invoke = GameReflection.FindPlainMethod(target.GetType(), "InvokeMethod", typeof(string), typeof(object[]));
            if (invoke == null) throw new MissingMethodException(target.GetType().Name, "InvokeMethod");
            return invoke.Invoke(target, new object[] { method, args ?? new object[0] });
        }

        /// <summary>ASCharacter.GetMember(name): an ASValue, or null.</summary>
        private static object GetMember(object target, string member)
        {
            MethodInfo get = GameReflection.FindPlainMethod(target.GetType(), "GetMember", typeof(string));
            if (get == null) throw new MissingMethodException(target.GetType().Name, "GetMember");
            return get.Invoke(target, new object[] { member });
        }

        /// <summary>ASCharacter.SetMember(name, value).</summary>
        private static void SetMember(object target, string member, object value)
        {
            MethodInfo set = GameReflection.FindPlainMethod(target.GetType(), "SetMember", typeof(string), typeof(object));
            if (set == null) throw new MissingMethodException(target.GetType().Name, "SetMember");
            set.Invoke(target, new[] { member, value });
        }

        /// <summary>
        /// ASValue.GetObject(null): by the value's own type (Number → float, Character → ASCharacter, Null → null). The ASValue is
        /// disposed (kept in <paramref name="owned"/> until the end).
        /// </summary>
        private static object Value(List<object> owned, object asValue)
        {
            if (asValue == null) return null;
            Own(owned, asValue);
            MethodInfo get = GameReflection.FindPlainMethod(asValue.GetType(), "GetObject", typeof(Type));
            if (get == null) throw new MissingMethodException(asValue.GetType().Name, "GetObject");
            return get.Invoke(asValue, new object[] { null });
        }

        private static double? Number(object value)
        {
            if (value is float) return (float)value;
            if (value is double) return (double)value;
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            if (value is uint) return (uint)value;
            return null;
        }

        private static object Own(List<object> owned, object wrapper)
        {
            if (wrapper == null) return null;
            // By reference: ASCharacter's Equals compares the native character, and every wrapper needs its own Dispose.
            foreach (object o in owned)
            {
                if (ReferenceEquals(o, wrapper)) return wrapper;
            }
            owned.Add(wrapper);
            return wrapper;
        }

        private static string Text(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static void Write(Log log, string line)
        {
            try
            {
                if (log != null) log.Info(line);
            }
            catch (Exception)
            {
            }
        }
    }
}
