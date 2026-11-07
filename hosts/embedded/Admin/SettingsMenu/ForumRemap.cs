// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent (reflection on Gameloft.Engine): compiled into the embedded DLL and the net8 tests.
using System;
using System.Collections;
using System.Reflection;

namespace McvsOffline.Embedded.Admin.SettingsMenu
{
    /// <summary>
    /// Build 22 (docs/design/settings-menu.md §3.2): the engine's own LocId remap turns Android's stock FORUM title into PERFORMANCE,
    /// so <c>MenuSettings.setup()</c>'s own dress of btn_forum (AND-AS MenuSettings.as:114-119, BtnBase.as:119) writes the translated
    /// PERFORMANCE on every focus-in, synchronously, and no render can show FORUM whatever the timing of our steps.
    /// <para>The remap: <c>LocalizationFormatterComponent.remapDictionary</c>, a private <c>Dictionary&lt;LocId, LocId&gt;</c>
    /// (LocalizationFormatterComponent.cs:34) filled once at OnCreate from Remap.json (:91-97, :269-292) and applied by
    /// <c>RemapLocId</c> inside both FormatString overloads before the string lookup (:260-267, :422, :494). Every text of the menus
    /// goes through it: MenuComponent.FormatString → LocalizationComponent.FormatString → <c>World.LocalizationFormatter</c>
    /// (MenuComponent.cs:1168-1205, LocalizationComponent.cs:335-338, World.cs:438), and ReloadStringTable re-formats every
    /// registered text field the same way after a language change (MenuComponent.cs:1047-1078). <c>LocId</c> converts from a
    /// string (<c>op_Implicit(string)</c>, LocId.cs:128) and compares by its full id (:148-160). "setting.ForumButton" is used only
    /// by that one dress (AND-AS :117; nowhere in the game's C#).</para>
    /// <para>Copy and swap: a new dictionary with the entry added (or removed) replaces the field, so a lookup on another thread
    /// sees the old or the new one whole, never one being changed. (RemapLocId reads the field twice, ContainsKey then the indexer:
    /// a swap between the two on another thread could only hurt a lookup of setting.ForumButton itself, which only the client
    /// World's menu formats, on the game thread, where the swap runs too.) Android only (Steam has its own btn_performance);
    /// pure C#, no Flash call, so it needs no same-frame switch. Never throws.</para>
    /// </summary>
    internal static class ForumRemap
    {
        /// <summary>The stock dress's title (AND-AS MenuSettings.as:117).</summary>
        public const string From = SettingsMenuLayout.ForumTitle;

        /// <summary>The PERFORMANCE title (the game's own string, the GRAPHICS SETTINGS value "PERFORMANCE").</summary>
        public const string To = SettingsMenuNames.PerformanceTitle;

        /// <summary>The private field (LocalizationFormatterComponent.cs:34).</summary>
        internal const string Field = "remapDictionary";

        /// <summary>
        /// Puts the entry in (<paramref name="on"/>) or takes it out of the client World's remap, when it is not so already. Null
        /// when the remap is as asked, else why not ("no World", "no LocalizationFormatter", "no remapDictionary", the exception).
        /// </summary>
        public static string Ensure(object ui, bool on)
        {
            return Ensure(ui, From, To, on);
        }

        /// <summary>The entry is in the client World's remap now (a read; false when it cannot be read).</summary>
        public static bool IsOn(object ui)
        {
            return IsOn(ui, From, To);
        }

        /// <summary>
        /// Build 23: <see cref="Ensure(object, bool)"/> for any one entry <paramref name="from"/> → <paramref name="to"/> (FORUM's, and
        /// <see cref="MoreGamesRemap"/>'s). Off takes out only that entry when it maps to <paramref name="to"/>. Never throws.
        /// </summary>
        internal static string Ensure(object ui, string from, string to, bool on)
        {
            try
            {
                object formatter;
                FieldInfo field;
                IDictionary map;
                string problem = Find(ui, out formatter, out field, out map);
                if (problem != null) return problem;
                Type key = KeyType(map.GetType());
                MethodInfo convert = key == null ? null : GameReflection.FindStaticMethod(key, "op_Implicit", typeof(string));
                if (convert == null || convert.ReturnType != key) return "no LocId from a string";
                object fromId = convert.Invoke(null, new object[] { from });
                object toId = convert.Invoke(null, new object[] { to });
                // Off takes out only our own entry (a Remap.json entry for the same id would be the game's).
                bool there = map.Contains(fromId) && Equals(map[fromId], toId);
                if (there == on) return null;
                // A new dictionary of the field's own type (its parameterless constructor; filled through IDictionary).
                ConstructorInfo create = field.FieldType.GetConstructor(Type.EmptyTypes);
                var copy = create == null ? null : create.Invoke(null) as IDictionary;
                if (copy == null) return "the remap cannot be copied";
                foreach (DictionaryEntry e in map)
                {
                    if (!on && Equals(e.Key, fromId)) continue;
                    copy[e.Key] = e.Value;
                }
                if (on) copy[fromId] = toId;
                field.SetValue(formatter, copy);
                return null;
            }
            catch (Exception ex)
            {
                Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                return cause.GetType().Name + ": " + cause.Message;
            }
        }

        /// <summary>The entry <paramref name="from"/> → <paramref name="to"/> is in the client World's remap now (false when it cannot be read).</summary>
        internal static bool IsOn(object ui, string from, string to)
        {
            try
            {
                object formatter;
                FieldInfo field;
                IDictionary map;
                if (Find(ui, out formatter, out field, out map) != null) return false;
                Type key = KeyType(map.GetType());
                MethodInfo convert = key == null ? null : GameReflection.FindStaticMethod(key, "op_Implicit", typeof(string));
                if (convert == null) return false;
                object fromId = convert.Invoke(null, new object[] { from });
                return map.Contains(fromId) && Equals(map[fromId], convert.Invoke(null, new object[] { to }));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Find(object ui, out object formatter, out FieldInfo field, out IDictionary map)
        {
            formatter = null;
            field = null;
            map = null;
            if (ui == null) return "no UI";
            object world = GameReflection.WorldOf(ui);
            if (world == null) return "no World";
            formatter = GameReflection.Property(world, "LocalizationFormatter");
            if (formatter == null) return "no LocalizationFormatter";
            for (Type type = formatter.GetType(); type != null && field == null; type = type.BaseType)
                field = type.GetField(Field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (field == null || !typeof(IDictionary).IsAssignableFrom(field.FieldType)) return "no " + Field;
            map = field.GetValue(formatter) as IDictionary;
            return map == null ? "no " + Field : null;
        }

        /// <summary>
        /// The dictionary's key type, from its indexer (the game's trimmed mscorlib has Type.GetGenericArguments as a throwing stub,
        /// EmbeddedDllResolutionTests), or null.
        /// </summary>
        private static Type KeyType(Type dictionary)
        {
            foreach (PropertyInfo p in dictionary.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.Name != "Item") continue;
                ParameterInfo[] index = p.GetIndexParameters();
                if (index.Length == 1 && index[0].ParameterType != typeof(object)) return index[0].ParameterType;
            }
            return null;
        }
    }

    /// <summary>
    /// Build 23 (owner, build 22 on the S22: ABOUT sits right of centre; docs/design/settings-menu.md §3.2, "Build 23"): the same
    /// engine remap as <see cref="ForumRemap"/>, for MORE GAMES' title: <c>setting.FreeGamesButton</c> → <c>setting.CreditButton</c>,
    /// so setup()'s own dress of btn_more (AND-AS MenuSettings.as:159-162) writes the translated ABOUT, and its own
    /// <c>btn_more.visible = true</c> and <c>x = -177</c> (:210, :212) draw ABOUT at Gameloft's one-button centre. The id is used
    /// only there (one string in and_UI.swf, none in and_lib.swf; MenuAbout.as:46 uses setting.CreditButton); the remap is
    /// single-hop (LocalizationFormatterComponent.cs:260-265). Installed only by <see cref="SettingsMenuLayout.PreDress"/>, before
    /// setup(), and taken out again in the same call when any step of the dress fails; Android only. MORE GAMES' tap keys on
    /// <see cref="IsOn"/> (<see cref="AboutRedirect"/>), so the title and the tap always agree. Never throws.
    /// </summary>
    internal static class MoreGamesRemap
    {
        public const string From = SettingsMenuNames.FreeGamesTitle;

        public const string To = SettingsMenuNames.CreditTitle;

        /// <summary>The entry in (<paramref name="on"/>) or out; null when it is as asked, else why not.</summary>
        public static string Ensure(object ui, bool on)
        {
            return ForumRemap.Ensure(ui, From, To, on);
        }

        /// <summary>The entry is in the client World's remap now (false when it cannot be read).</summary>
        public static bool IsOn(object ui)
        {
            return ForumRemap.IsOn(ui, From, To);
        }
    }
}
