// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent (reflection on Gameloft.Engine and Game): compiled into the embedded DLL and the net8 tests.
using System;
using System.Reflection;
using McvsOffline.Server.Core;

namespace McvsOffline.Embedded.Admin.SettingsMenu
{
    /// <summary>
    /// Build 23 (owner, build 22 on the S22: "ABOUT sits right of centre"; docs/design/settings-menu.md §3.2, "Build 23"): the tap of
    /// MORE GAMES' button while it is drawn as ABOUT (<see cref="MoreGamesRemap"/> in). <c>GoToFreeGames</c> is the native of two
    /// buttons: MORE GAMES (AND-AS MenuSettings.as:279-281) and the top bar's Gameloft button (FreemiumBar.as:566-570), so the tap
    /// is told apart by the focus, which ButtonSimple gives the button it releases (ButtonSimple.as:265, :302 giveFocus; :329 the
    /// RELEASE; only :342 clears it afterwards), read inside the native from <c>gluic.utils.FocusManager.componentWithFocus</c> (a
    /// static getter, FocusManager.as:19-22) and compared by ASCharacter.Equals with Settings' <c>btn_more</c>:
    /// <list type="bullet">
    /// <item>Focus on btn_more: ABOUT, posted (a push re-enters the menu stack; PerformancePage does the same), while Settings is on
    /// top; else "ABOUT not opened" (a second tap finds menu_about on top).</item>
    /// <item>Focus on anything else (the Gameloft button): the game's own <c>UIComponent.GoToFreeGames()</c> at once.</item>
    /// <item>Focus unreadable (the same-frame switch off, no FocusManager, not an ASCharacter, an exception; one line per process):
    /// decided by the top menu in the posted step: Settings on top → ABOUT, menu_about on top → not opened (a second tap), anything
    /// else → the game's GoToFreeGames.</item>
    /// </list>
    /// Reading the focus is Flash work inside a native: only while <see cref="SettingsMenuLayout.InNativeAllowed"/>, and the
    /// process's first read only after its own breadcrumb (<see cref="SameFrameBreadcrumb.EnterTap"/>: the focus-in's marker is
    /// always spent by then, and the static getter is a kind of call no earlier in-native step makes), so a native fault in it
    /// switches <c>admin.settingsMenu.sameFrame</c> off at the next start and the top menu decides from then on. Android only.
    /// Never throws.
    /// </summary>
    internal static class AboutRedirect
    {
        /// <summary>Inside the native: read the focus, then ABOUT (posted) or the game's MORE GAMES.</summary>
        public static void Tap(ISettingsMenuHost host, SettingsMenuState state, object ui)
        {
            try
            {
                if (host == null || state == null || ui == null) return;
                string unreadable;
                string focus = Focus(host, state, ui, out unreadable);
                if (unreadable == null && focus != SettingsMenuNames.MoreGamesButton)
                {
                    Write(host, "settings menu: MORE GAMES tap passed to the game (focus on " + focus + ")");
                    FlashInterop.GoToFreeGames(ui);
                    return;
                }
                if (unreadable != null && state.BeginFocusUnreadableLog())
                    Write(host, "settings menu: focus unreadable (" + unreadable + "); MORE GAMES taps decided by the top menu");
                if (!host.Post(() => Push(host, unreadable)))
                    Write(host, "settings menu: ABOUT not opened (no game thread)");
            }
            catch (Exception ex)
            {
                Write(host, "settings menu: ABOUT not opened (" + Reason(ex) + ")");
            }
        }

        /// <summary>
        /// The focused component's name ("btn_more" when it is Settings' MORE GAMES), or null with <paramref name="unreadable"/> saying
        /// why it could not be told. Reads only.
        /// </summary>
        private static string Focus(ISettingsMenuHost host, SettingsMenuState state, object ui, out string unreadable)
        {
            unreadable = null;
            if (!SettingsMenuLayout.InNativeAllowed(host, state))
            {
                unreadable = "same frame off";
                return null;
            }
            try
            {
                SameFrameBreadcrumb.EnterTap(host, state, SettingsMenuNames.GoToFreeGames);
                using (var flash = new FlashScope(ui))
                {
                    object manager = flash.Class(SettingsMenuNames.FocusManagerPackage, SettingsMenuNames.FocusManagerClass);
                    if (manager == null)
                    {
                        unreadable = "no " + SettingsMenuNames.FocusManagerClass;
                        return null;
                    }
                    object focused = flash.Get(manager, SettingsMenuNames.FocusMember);
                    // Only a display object is an answer: null, undefined, a function or any plain value is not.
                    if (focused == null || GameReflection.FindPlainMethod(focused.GetType(), "GetMember", typeof(string)) == null)
                    {
                        unreadable = focused == null ? "null" : focused.GetType().Name;
                        return null;
                    }
                    object panel = SettingsMenuLayout.SettingsPanel(flash);
                    object more = panel == null ? null : flash.Child(panel, SettingsMenuNames.MoreGamesButton);
                    if (more != null && focused.Equals(more)) return SettingsMenuNames.MoreGamesButton;
                    return SettingsMenuLayout.NameOf(flash, focused) ?? "an unnamed component";
                }
            }
            catch (Exception ex)
            {
                Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                unreadable = cause.GetType().Name;
                return null;
            }
        }

        /// <summary>Game thread, the frame after the tap: ABOUT while Settings is on top (see the class).</summary>
        private static void Push(ISettingsMenuHost host, string unreadable)
        {
            try
            {
                object ui = host.ClientUi();
                if (ui == null)
                {
                    Write(host, "settings menu: ABOUT not opened (no UI)");
                    return;
                }
                using (var flash = new FlashScope(ui))
                {
                    string top = TopMenu(flash);
                    if (top == SettingsMenuNames.SettingsMenu)
                    {
                        object manager = flash.Class("managers", "MenusManager");
                        if (manager == null)
                        {
                            Write(host, "settings menu: ABOUT not opened (no MenusManager)");
                            return;
                        }
                        flash.Call(manager, "pushMenu", SettingsMenuNames.AboutMenu);
                        Write(host, "settings menu: ABOUT opened from MORE GAMES (" +
                                    (unreadable == null ? "focus " + SettingsMenuNames.MoreGamesButton : "focus unreadable: " + unreadable + ", Settings on top") + ")");
                        return;
                    }
                    if (unreadable == null || top == SettingsMenuNames.AboutMenu)
                    {
                        Write(host, "settings menu: ABOUT not opened (the menu on top is " + (top ?? "unnamed") + ")");
                        return;
                    }
                }
                Write(host, "settings menu: MORE GAMES tap passed to the game (Settings not on top)");
                FlashInterop.GoToFreeGames(ui);
            }
            catch (Exception ex)
            {
                Write(host, "settings menu: ABOUT not opened (" + Reason(ex) + ")");
            }
        }

        /// <summary>The name of the menu on top of MenusStack, or null. Reads only.</summary>
        private static string TopMenu(FlashScope flash)
        {
            object stack = flash.Class("gluic.components.menus", "MenusStack");
            if (stack == null) return null;
            object menu = flash.Call(stack, "getMenuOnTopOfStack");
            return menu == null ? null : SettingsMenuLayout.NameOf(flash, menu);
        }

        private static string Reason(Exception ex)
        {
            Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            return cause.GetType().Name + ": " + cause.Message;
        }

        private static void Write(ISettingsMenuHost host, string line)
        {
            try
            {
                Log log = host == null ? null : host.Log;
                if (log != null) log.Info(line);
            }
            catch (Exception)
            {
            }
        }
    }
}
