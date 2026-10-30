using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded.SettingsMenu
{
    /// <summary>
    /// A fake Game.UIComponent with the Flash side of the two Settings menus, for docs/design/settings-menu.md §4.1 (WP-A to WP-D):
    /// <list type="bullet">
    /// <item><b>The native table</b> (<see cref="FakeNativeTable"/>, the real dispatch rules) with the game's own delegates registered
    /// (<see cref="FakeGameNatives"/>); <see cref="GoToForum"/> and <see cref="ShowGenericConfirmPopup"/> as UIComponent has them.</item>
    /// <item><b>The engine's Flash interop</b> by the real member names (<c>FindClass</c>; <see cref="FlashCharacter"/>,
    /// <see cref="FlashClass"/>, <see cref="FlashValue"/> for ASCharacter, ASClass, ASValue: <c>InvokeMethod</c>, <c>GetMember</c>,
    /// <c>SetMember</c>, <c>GetObject(Type)</c>, <c>Name</c>, <c>CurrentFrame</c>, IDisposable), with ASValue's conversions: an AS
    /// Number reads as float, a double or long written becomes an opaque managed object (<see cref="FlashManaged"/>).</item>
    /// <item><b>menu_settings</b>: <c>mc_panel</c> with frames 1-5 of both SWFs (instance names and positions from the panel
    /// timelines, work/and_settings_frames.txt and work/pc_settings_frames.txt: Android sprite 6718, Steam 7035), and what
    /// <c>MenuSettings.onFocusIn</c> does: <c>EnterSection</c> first, then <c>setup()</c> (the tiles' <c>setup({title, icon})</c>, Android's
    /// absolute bottom row, Steam's 4-column x and the relative CUSTOMER CARE/ABOUT drift of PC MenuSettings.as:253-258), ending with
    /// <c>GetPlatformId</c> twice. <see cref="Tap"/> runs <c>onButtonReleased</c> (hidden or mouse-disabled instances take no tap).</item>
    /// <item><b>menu_gameplaySettings</b>: <c>MenuGameplaySettings.setup()</c> as the Android AS builds it (AND-AS/MGS:82-436):
    /// <c>initOptions()</c>, <c>CanChangeMaxFramerate</c> (push of MaxFramerate) then <c>CanChangeGraphicSetting</c> (splice at 2), the
    /// row loop with <c>categoryMC</c> headers and <c>optionMC</c> rows (<c>item&lt;k&gt;</c>, <c>y += height + 8</c>), and the change
    /// handlers: <c>List.displayList</c>, <c>List.dataLength</c> and <c>ScrollBar.value=</c> fire CHANGE when the value differs
    /// (List.as:1362-1366, ScrollBar.as:336-353, UIComponent.as:317-329), <c>onListSelectorChange</c> (:523-576: the int-passing types,
    /// <c>Boolean(index)</c> for the rest, only when initialised, then always <c>SaveUserPreferences</c>), <c>onBarScroll</c> (:487-509, no
    /// initialised guard). Listeners are attached where the AS attaches them.</item>
    /// </list>
    /// Every write and mutating call that comes through a handle (the host's) is in <see cref="HostWrites"/>; the simulated
    /// ActionScript writes directly. Heights of rows and headers and ThinBtn's runtime width are guesses [G] (not in the timelines).
    /// </summary>
    internal sealed class FakeFlashTree : FakeNativeTable
    {
        public const float TileWidth = 465.35f;
        public const float TileHeight = 355.05f;

        /// <summary>ThinBtn's runtime <c>.width</c> (grid.md §2: about 401, txt and mc_new stick out of the 365.8 plate) [I].</summary>
        public const float ThinButtonWidth = 401f;

        public const float RowHeight = 96f;
        public const float CategoryHeight = 60f;

        private readonly Dictionary<string, Dictionary<string, Func<object[], object>>> classes =
            new Dictionary<string, Dictionary<string, Func<object[], object>>>(StringComparer.Ordinal);

        private int instances;

        public FakeFlashTree(bool steam = false, bool registerGameNatives = true)
        {
            Steam = steam;
            Game = new FakeGameNatives { PlatformId = steam ? 6 : 2 };
            if (registerGameNatives) Game.RegisterAll(this);
            classes["gluic.components.menus.MenusStack"] = new Dictionary<string, Func<object[], object>>
            {
                { "getMenuOnTopOfStack", a => Top },
            };
            classes["managers.MenusManager"] = new Dictionary<string, Func<object[], object>>
            {
                { "pushMenu", a => { PushMenu(a.Length > 0 ? a[0] as string : null); return null; } },
                { "popMenu", a => { PopMenu(); return null; } },
            };
            classes["managers.VPManager"] = new Dictionary<string, Func<object[], object>>
            {
                { "getViewportBoundX", a => 0f },
                { "getViewportBoundWidth", a => 2048f },
            };
            classes["shared.utilities.TextUtilities"] = new Dictionary<string, Func<object[], object>>
            {
                { "setStringID", a => { SetStringId(a.Length > 0 ? a[0] as FlashObject : null, a.Length > 1 ? a[1] : null, a.Skip(2).ToArray()); return null; } },
            };
            classes["elements.freemiumBar.FreemiumBar"] = new Dictionary<string, Func<object[], object>>
            {
                { "updateTitle", a => { FreemiumTitles.Add(a.Length > 0 ? a[0] as string : null); return null; } },
            };
        }

        /// <summary>Steam (platform 6, main frame 1) rather than Android (platform 2, main frame 3).</summary>
        public bool Steam { get; private set; }

        /// <summary>GameSettings.isInGame: Settings opens on frame 2 and Gameplay Settings on its frame 2.</summary>
        public bool InGame { get; set; }

        /// <summary>Native.IsKayac (Settings frame 4) and Native.IsChineseVersion (frame 5).</summary>
        public bool Kayac { get; set; }

        public bool Chinese { get; set; }

        /// <summary>The game's own delegates and preference store.</summary>
        public FakeGameNatives Game { get; private set; }

        /// <summary>MenusStack: the last one is on top.</summary>
        public List<FlashObject> Stack { get; } = new List<FlashObject>();

        public FlashObject Top { get { return Stack.Count == 0 ? null : Stack[Stack.Count - 1]; } }

        /// <summary>MenusManager.pushMenu names, in order (Flash's own pushes and the host's).</summary>
        public List<string> Pushed { get; } = new List<string>();

        public List<string> FreemiumTitles { get; } = new List<string>();

        /// <summary>"path.member=value" and "path.method(args)" for everything the host wrote or called through a handle or class.</summary>
        public List<string> HostWrites { get; } = new List<string>();

        /// <summary>Doubles and longs the host wrote: ASValue.SetObject makes them managed objects, not AS Numbers.</summary>
        public List<string> ManagedWrites { get; } = new List<string>();

        public List<FakePopup> Popups { get; } = new List<FakePopup>();

        /// <summary>onShowFramerateWarning ran (the 'i' popup, or a MaxFramerate change above index 0).</summary>
        public int FramerateWarnings { get; private set; }

        /// <summary>MenuGameplaySettings._isInitialized of the page on top.</summary>
        public bool GameplayInitialized { get; private set; }

        /// <summary>Taps that reached a handler (instance names).</summary>
        public List<string> Taps { get; } = new List<string>();

        /// <summary>Every handle, class and value handed to the host.</summary>
        internal List<IFlashWrapper> Handed { get; } = new List<IFlashWrapper>();

        /// <summary>Generic InvokeMethod&lt;T&gt;/GetMember&lt;T&gt; calls (the host must use the plain overloads).</summary>
        public int GenericCalls { get; set; }

        // ------------------------------------------------------------------ UIComponent

        /// <summary>MenuComponent.FindClass(package, class): a new ASClass handle, or null.</summary>
        public FlashClass FindClass(string packageName, string className)
        {
            string name = packageName + "." + className;
            if (!classes.ContainsKey(name)) return null;
            return Track(new FlashClass(this, name));
        }

        /// <summary>Removes an AS class (FindClass then returns null).</summary>
        public void RemoveClass(string fullName)
        {
            classes.Remove(fullName);
        }

        /// <summary>Replaces or adds one static function of an AS class.</summary>
        public void SetStatic(string fullName, string function, Func<object[], object> body)
        {
            Dictionary<string, Func<object[], object>> statics;
            if (!classes.TryGetValue(fullName, out statics)) classes[fullName] = statics = new Dictionary<string, Func<object[], object>>(StringComparer.Ordinal);
            if (body == null) statics.Remove(function);
            else statics[function] = body;
        }

        internal object CallStatic(string fullName, string function, object[] args)
        {
            Dictionary<string, Func<object[], object>> statics;
            Func<object[], object> body;
            if (!classes.TryGetValue(fullName, out statics) || !statics.TryGetValue(function, out body)) return null;
            return body(args);
        }

        /// <summary>UIComponent.GoToForum(): the game's body (Browser.ShowForum, or patch 'forum').</summary>
        public void GoToForum()
        {
            Game.ForumCalls++;
        }

        /// <summary>UIComponent.ShowGenericConfirmPopup (UIComponent.cs:1854): recorded; <see cref="FakePopup.Answer"/> calls back.</summary>
        public void ShowGenericConfirmPopup(string eventName, Dictionary<string, object> data, Action<Dictionary<string, object>> del = null,
                                            FakePopupPriority priority = FakePopupPriority.AtEnd)
        {
            Popups.Add(new FakePopup(eventName, data == null ? null : new Dictionary<string, object>(data), del, priority));
        }

        /// <summary>The async overload the game also has (UIComponent.cs:1871): a host must not pick it.</summary>
        public Task<bool> ShowGenericConfirmPopup(string eventName, Dictionary<string, object> data, FakePopupPriority priority = FakePopupPriority.AtEnd,
                                                  CancellationToken ct = default(CancellationToken))
        {
            throw new InvalidOperationException("the async overload is not the one to use");
        }

        // ------------------------------------------------------------------ menus

        /// <summary>MenusManager.pushMenu(name): Settings and Gameplay Settings are built and focused in; any other name is a bare menu.</summary>
        public FlashObject PushMenu(string name)
        {
            Pushed.Add(name);
            FlashObject below = Top;
            if (below != null && below.Name == McvsMenuNames.Gameplay) EmptyContent(below);
            FlashObject menu;
            if (name == McvsMenuNames.Settings) menu = BuildSettingsMenu(MainFrame());
            else if (name == McvsMenuNames.Gameplay) menu = BuildGameplayMenu();
            else menu = new FlashObject(this, "Menu", name);
            Stack.Add(menu);
            FocusIn(menu);
            return menu;
        }

        /// <summary>MenusManager.popMenu: the menu on top goes (Gameplay Settings saves its preferences, MGS:711-717) and the one below is focused in again.</summary>
        public void PopMenu()
        {
            FlashObject top = Top;
            if (top == null) return;
            Stack.RemoveAt(Stack.Count - 1);
            if (top.Name == McvsMenuNames.Gameplay)
            {
                CallFromFlash("SaveUserPreferences", null);
                EmptyContent(top);
            }
            if (Top != null) FocusIn(Top);
        }

        /// <summary>Opens Settings (the platform's frame: in game 2, Kayac 4, Chinese 5, else 1 on Steam and 3 on Android).</summary>
        public FlashObject OpenSettings()
        {
            return PushMenu(McvsMenuNames.Settings);
        }

        /// <summary>Opens Settings on a given panel frame (both SWFs have frames 1-5), e.g. Android's frame 1 or Steam's frame 3.</summary>
        public FlashObject OpenSettingsAtFrame(int frame)
        {
            Pushed.Add(McvsMenuNames.Settings);
            FlashObject menu = BuildSettingsMenu(frame);
            Stack.Add(menu);
            FocusIn(menu);
            return menu;
        }

        /// <summary>The panel of the Settings menu on top (or below the page), or null.</summary>
        public FlashObject Panel
        {
            get
            {
                for (int i = Stack.Count - 1; i >= 0; i--)
                {
                    if (Stack[i].Name == McvsMenuNames.Settings) return Stack[i].Child("mc_panel");
                }
                return null;
            }
        }

        /// <summary>A Settings panel instance (null when the frame has none).</summary>
        public FlashObject Instance(string name)
        {
            FlashObject panel = Panel;
            return panel == null ? null : panel.Child(name);
        }

        /// <summary>MenuSettings.onButtonReleased for the instance (by name). False when it is absent, hidden or mouse-disabled.</summary>
        public bool Tap(string instance)
        {
            FlashObject button = Instance(instance);
            if (button == null || !Truthy(button.Get("visible")) || !Truthy(button.Get("mouseEnabled"))) return false;
            Taps.Add(instance);
            switch (instance)
            {
                case "btn_forum": CallFromFlash("GoToForum", null); break;
                case "btn_customer": CallFromFlash("GoToCostumerCare", null); break;
                case "btn_more": CallFromFlash("GoToFreeGames", null); break;
                case "btn_general": PushMenu(McvsMenuNames.Gameplay); break;
                case "btn_performance":
                    // Only platforms 4 and 6 have the listener (AND-AS/MS:305-309, :363-377).
                    if (Steam) PushMenu(McvsMenuNames.Gameplay);
                    break;
                case "btn_credits": PushMenu("menu_about"); break;
                case "btn_social": PushMenu("menu_socialSetting"); break;
                case "btn_account": PushMenu("menu_accounts"); break;
                case "btn_help": PushMenu("menu_help"); break;
                case "btn_language": PushMenu("menu_language"); break;
                case "btn_audio": PushMenu("menu_audio"); break;
            }
            return true;
        }

        /// <summary>Back from the menu on top.</summary>
        public void Back()
        {
            PopMenu();
        }

        private int MainFrame()
        {
            if (InGame) return 2;
            if (Steam) return 1;
            return Kayac ? 4 : Chinese ? 5 : 3;
        }

        private void FocusIn(FlashObject menu)
        {
            if (menu.Name == McvsMenuNames.Settings) FocusInSettings(menu);
            else if (menu.Name == McvsMenuNames.Gameplay) FocusInGameplay(menu);
        }

        // ------------------------------------------------------------------ menu_settings

        /// <summary>One timeline instance: name, kind (S BtnSetting, T ThinBtn, C ButtonClose, B blocker, L title), x, y, scale.</summary>
        private struct Placed
        {
            public string Name;
            public char Kind;
            public float X;
            public float Y;
            public float Scale;
        }

        private static Placed P(string name, char kind, float x, float y, float scale = 1f)
        {
            return new Placed { Name = name, Kind = kind, X = x, Y = y, Scale = scale };
        }

        // Android UI.swf sprite 6718 (work/and_settings_frames.txt), in depth order.
        private static readonly Placed[][] AndroidFrames =
        {
            new[]
            {
                P("btn_close", 'C', 387.15f, -405.85f), P("btn_forum", 'S', -227.65f, -50.9f), P("btn_help", 'S', -704.6f, -50.9f),
                P("btn_customize", 'S', 7.85f, -428.1f), P("btn_audio", 'S', -470.05f, -428.1f), P("btn_general", 'S', -947f, -428.1f),
                P("btn_credits", 'T', 258.05f, 354.1f), P("btn_more", 'T', -176.9f, 354.1f), P("btn_customer", 'T', -613.9f, 354.1f),
                P("btn_language", 'S', 250.25f, -50.9f), P("btn_social", 'S', 485.75f, -428.1f),
            },
            new[]
            {
                P("mc_blocker", 'B', 0f, -768f), P("btn_close", 'C', 708.55f, -365.85f), P("mc_title", 'L', 0f, -335.05f),
                P("btn_audio", 'S', -230.5f, -177.55f), P("btn_general", 'S', -712.65f, -177.55f), P("btn_customize", 'S', 251.65f, -177.55f),
            },
            new[]
            {
                P("btn_close", 'C', 387.15f, -405.85f), P("btn_forum", 'S', 6.35f, -50.9f), P("btn_help", 'S', -470.6f, -50.9f),
                P("btn_customize", 'S', 6.85f, -427f), P("btn_audio", 'S', -471.05f, -427.9f), P("btn_general", 'S', -948f, -427.9f),
                P("btn_credits", 'T', 258.05f, 354.1f), P("btn_language", 'S', 484.25f, -50.9f), P("btn_social", 'S', 484f, -427f),
                P("btn_more", 'T', -176.9f, 354.1f), P("btn_customer", 'T', -613.9f, 354.1f), P("btn_account", 'S', -948.7f, -50.9f),
            },
            new[]
            {
                P("btn_close", 'C', 387.15f, -405.85f), P("btn_help", 'S', -232.7f, -50.9f), P("btn_customize", 'S', 10.2f, -427f),
                P("btn_audio", 'S', -472.7f, -427.9f), P("btn_general", 'S', -955.6f, -427.9f), P("btn_credits", 'T', 258.05f, 354.1f),
                P("btn_language", 'S', 250.2f, -50.9f), P("btn_account", 'S', -715.6f, -50.9f), P("btn_more", 'T', -176.9f, 354.1f),
                P("btn_customer", 'T', -613.9f, 354.1f), P("btn_social", 'S', 493.1f, -428.1f),
            },
            new[]
            {
                P("btn_close", 'C', 387.15f, -405.85f), P("btn_credits", 'T', 258.05f, 354.1f), P("btn_more", 'T', -176.9f, 354.1f),
                P("btn_customer", 'T', -613.9f, 354.1f), P("btn_audio", 'S', -231.25f, -427.9f), P("btn_general", 'S', -714.15f, -427.9f),
                P("btn_help", 'S', 8.75f, -50.9f), P("btn_account", 'S', -474.15f, -50.9f), P("btn_customize", 'S', 251.65f, -427.9f),
            },
        };

        // Steam UI.swf sprite 7035 (work/pc_settings_frames.txt), in depth order.
        private static readonly Placed[][] SteamFrames =
        {
            new[]
            {
                P("btn_close", 'C', 387.15f, -405.85f), P("btn_forum", 'S', -227.65f, -50.9f), P("btn_help", 'S', -704.6f, -50.9f),
                P("btn_social", 'S', 249.85f, -427f), P("btn_audio", 'S', -228.05f, -427.9f), P("btn_general", 'S', -705f, -427.9f),
                P("btn_credits", 'T', 258.05f, 354.1f), P("btn_more", 'T', -176.9f, 354.1f), P("btn_customer", 'T', -613.9f, 354.1f),
                P("btn_language", 'S', 250.25f, -50.9f), P("btn_account", 'S', 733f, -427f), P("btn_performance", 'S', 733f, -50.9f),
            },
            new[]
            {
                P("mc_blocker", 'B', 0f, -768f), P("btn_close", 'C', 708.55f, -365.85f), P("mc_title", 'L', 0f, -335.05f),
                P("btn_audio", 'S', -230.5f, -177.55f), P("btn_general", 'S', -712.65f, -177.55f), P("btn_customize", 'S', 251.65f, -177.55f),
                P("btn_performance", 'S', -182.1f, -138.1f, 0.8f), P("btn_keyboard", 'S', 201f, -136.15f, 0.8f),
                P("btn_gamepad", 'S', 583.95f, -134.3f, 0.8f),
            },
            new[]
            {
                P("btn_close", 'C', 387.15f, -405.85f), P("btn_forum", 'S', -227.65f, -50.9f), P("btn_help", 'S', -704.6f, -50.9f),
                P("btn_social", 'S', 6.85f, -427f), P("btn_audio", 'S', -471.05f, -427.9f), P("btn_general", 'S', -948f, -427.9f),
                P("btn_credits", 'T', 258.05f, 354.1f), P("btn_language", 'S', 250.25f, -50.9f), P("btn_account", 'S', 484f, -427f),
                P("btn_more", 'T', -176.9f, 354.1f), P("btn_customer", 'T', -613.9f, 354.1f),
            },
            new[]
            {
                P("btn_close", 'C', 387.15f, -405.85f), P("btn_help", 'S', -232.7f, -50.9f), P("btn_social", 'S', 250.2f, -427f),
                P("btn_audio", 'S', -232.7f, -427.9f), P("btn_general", 'S', -715.6f, -427.9f), P("btn_credits", 'T', 258.05f, 354.1f),
                P("btn_language", 'S', 250.2f, -50.9f), P("btn_account", 'S', -715.6f, -50.9f), P("btn_more", 'T', -176.9f, 354.1f),
                P("btn_customer", 'T', -613.9f, 354.1f),
            },
            new[]
            {
                P("btn_close", 'C', 387.15f, -405.85f), P("btn_credits", 'T', 258.05f, 354.1f), P("btn_more", 'T', -176.9f, 354.1f),
                P("btn_customer", 'T', -613.9f, 354.1f), P("btn_audio", 'S', 8.75f, -427.9f), P("btn_general", 'S', -474.15f, -427.9f),
                P("btn_help", 'S', 8.75f, -50.9f), P("btn_account", 'S', -474.15f, -50.9f),
            },
        };

        /// <summary>The instance names of a panel frame (1-5) in depth order, as the timeline places them.</summary>
        public static IList<string> FrameInstances(bool steam, int frame)
        {
            return (steam ? SteamFrames : AndroidFrames)[frame - 1].Select(p => p.Name).ToList();
        }

        /// <summary>A timeline position (x, y) of a frame's instance, or null.</summary>
        public static float[] TimelinePosition(bool steam, int frame, string name)
        {
            foreach (Placed p in (steam ? SteamFrames : AndroidFrames)[frame - 1])
            {
                if (p.Name == name) return new[] { p.X, p.Y };
            }
            return null;
        }

        private FlashObject BuildSettingsMenu(int frame)
        {
            var menu = new FlashObject(this, "Menu", McvsMenuNames.Settings);
            menu.Members["title"] = "menu.SettingButton";
            var panel = new FlashObject(this, "MovieClip", "mc_panel") { Frame = frame };
            menu.Add(panel);
            foreach (Placed p in (Steam ? SteamFrames : AndroidFrames)[frame - 1])
            {
                string kind = p.Kind == 'S' ? "BtnSetting" : p.Kind == 'T' ? "ThinBtn" : p.Kind == 'C' ? "ButtonClose" : "MovieClip";
                var item = new FlashObject(this, kind, p.Name);
                item.Members["x"] = p.X;
                item.Members["y"] = p.Y;
                item.Members["scaleX"] = p.Scale;
                item.Members["scaleY"] = p.Scale;
                if (p.Kind == 'S')
                {
                    item.Members["width"] = TileWidth * p.Scale;
                    item.Members["height"] = TileHeight * p.Scale;
                    item.Add(new FlashObject(this, "TextField", "txt"));
                    item.Add(new FlashObject(this, "MovieClip", "mc_image"));
                    FlashObject tile = item;
                    item.Methods["setup"] = a => { TileSetup(tile, a.Length > 0 ? a[0] : null); return null; };
                }
                else if (p.Kind == 'T')
                {
                    item.Members["width"] = ThinButtonWidth;
                    item.Members["height"] = 61.65f;
                    item.Add(new FlashObject(this, "TextField", "txt"));
                    FlashObject thin = item;
                    item.Methods["setup"] = a => { TileSetup(thin, a.Length > 0 ? a[0] : null); return null; };
                }
                panel.Add(item);
            }
            return menu;
        }

        /// <summary>BtnBase.setup(dict): the title through TextUtilities.setStringID (a GetPlatformId native) and the icon.</summary>
        private void TileSetup(FlashObject tile, object data)
        {
            var dict = data as IDictionary<string, object>;
            if (dict == null) return;
            object title;
            if (dict.TryGetValue("title", out title))
            {
                tile.Members["title"] = title;
                SetStringId(tile.Child("txt"), title, new object[0]);
            }
            object icon;
            if (dict.TryGetValue("icon", out icon)) tile.Members["icon"] = icon;
        }

        private static readonly Dictionary<string, string[]> StockTiles = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "btn_general", new[] { "setting.GamePlaySetButton", "Menus/assets/png/icons/settings_gameplay.png" } },
            { "btn_social", new[] { "setting.SocialMediaButton", "Menus/assets/png/icons/settings_medias.png" } },
            { "btn_audio", new[] { "setting.AudioButton", "Menus/assets/png/icons/settings_sounds.png" } },
            { "btn_customize", new[] { "setting.CustomizeHUD", "Menus/assets/png/icons/settings_custom_hud.png" } },
            { "btn_language", new[] { "setting.LauguagesTxt", "Menus/assets/png/icons/settings_language.png" } },
            { "btn_forum", new[] { "setting.ForumButton", "Menus/assets/png/icons/settings_forum.png" } },
            { "btn_help", new[] { "setting.HelpButton", "Menus/assets/png/icons/settings_notif.png" } },
            { "btn_account", new[] { "setting.AccountsButton", "Menus/assets/png/icons/settings_account.png" } },
            { "btn_credits", new[] { "setting.CreditButton", null } },
            { "btn_customer", new[] { "setting.CustomerCareButton", null } },
            { "btn_more", new[] { "setting.FreeGamesButton", null } },
            { "btn_keyboard", new[] { "settingWindows.KeyboardSettings", "Menus/assets/png/icons/keyboard_gameplay.png" } },
            { "btn_gamepad", new[] { "settingWindows.ControllerSettings", "Menus/assets/png/icons/controller_gameplay.png" } },
            { "btn_performance", new[] { "settingWindows.PerformanceTitle", "Menus/assets/png/icons/settings_performance.png" } },
        };

        /// <summary>MenuSettings.onFocusIn: MenuBase.onFocusIn's EnterSection, then setup() (AND-AS/MS:468-473, PC-AS/MS:640-645).</summary>
        private void FocusInSettings(FlashObject menu)
        {
            FlashObject panel = menu.Child("mc_panel");
            CallFromFlash("EnterSection", null, panel.Frame == 2 ? "pvp_settings_screen" : "settings_main_screen");
            CallFromFlash("MoveToCamera", null, "settings.voidLoaded", "VoidCamera");
            foreach (FlashObject item in panel.Children)
            {
                string[] stock;
                if (item.Name == "btn_keyboard" || item.Name == "btn_gamepad" || item.Name == "btn_performance")
                {
                    if (!Steam) continue;   // the platform 4/6 block (AND-AS/MS:167-207)
                }
                if (StockTiles.TryGetValue(item.Name ?? "", out stock))
                {
                    var data = new Dictionary<string, object>(StringComparer.Ordinal) { { "title", stock[0] } };
                    if (stock[1] != null) data["icon"] = stock[1];
                    item.Call("setup", new object[] { data });
                }
            }
            FlashObject more = panel.Child("btn_more");
            FlashObject customer = panel.Child("btn_customer");
            FlashObject credits = panel.Child("btn_credits");
            if (!Steam)
            {
                // AND-AS/MS:180-184: absolute.
                if (more != null && customer != null && credits != null)
                {
                    bool shown = !Kayac;
                    more.Members["visible"] = shown;
                    customer.Members["x"] = shown ? -564f : -365f;
                    more.Members["x"] = -177f;
                    credits.Members["x"] = shown ? 208f : 22f;
                }
            }
            else if (panel.Frame == 1)
            {
                // PC-AS/MS:228-365 on Steam: the 4-column x, and the relative drift of CUSTOMER CARE and ABOUT (:253-258).
                SetX(panel, "btn_general", -948f);
                if (more != null && customer != null && credits != null)
                {
                    more.Members["visible"] = false;
                    customer.Members["x"] = customer.Num("x") + more.Num("width") / 2;
                    credits.Members["x"] = credits.Num("x") - more.Num("width") / 2;
                }
                SetX(panel, "btn_audio", -471.05f);
                SetX(panel, "btn_social", 6.85f);
                SetX(panel, "btn_account", 484f);
                SetX(panel, "btn_help", -948f);
                SetX(panel, "btn_forum", -471.05f);
                SetX(panel, "btn_language", 6.85f);
                SetX(panel, "btn_performance", 484f);
            }
            // addButtonListeners: the last natives of setup() (AND-AS/MS:363, PC-AS/MS:530).
            CallFromFlash("GetPlatformId", Game.PlatformId);
            CallFromFlash("GetPlatformId", Game.PlatformId);
        }

        private static void SetX(FlashObject panel, string name, float x)
        {
            FlashObject item = panel.Child(name);
            if (item != null) item.Members["x"] = x;
        }

        // ------------------------------------------------------------------ menu_gameplaySettings

        /// <summary>One _arOptions descriptor.</summary>
        internal sealed class Option
        {
            public string Title;
            public string Type;
            public string Category;
            public string Setup;
            public List<object> Values;
        }

        private static Option O(string title, string type, string category, string setup, params string[] values)
        {
            return new Option { Title = title, Type = type, Category = category, Setup = setup, Values = values.Length == 0 ? null : values.Cast<object>().ToList() };
        }

        /// <summary>initOptions() (AND-AS/MGS:82-213), no gamepad connected.</summary>
        internal static List<Option> InitOptions()
        {
            const string controls = "setting.Controls";
            const string general = "menu.GeneralTxt";
            const string advanced = "setting.AdvancedOptions";
            const string gamepad = "setting.CustomizeSettingsGamepad";
            string off = "setting.OffButton", on = "setting.OnButton";
            return new List<Option>
            {
                O("setting.ControlMode", "EnableAutoShoot", controls, "selector", "setting.StandardControl", "setting.AdvancedControl"),
                O("setting.IronSight", "IronSightMode", controls, "selector", "setting.DoubleTap", "setting.IronButton", "setting.BothTxt"),
                O("setting.CameraSensitivity", "CameraSensitivity", general, "slider"),
                O("setting.CameraSmooth", "CameraSmoothing", general, "slider"),
                O("setting.SprintSensi", "SprintSensitivity", general, "slider"),
                O("setting.FOV", "PlayerFov", general, "slider"),
                O("setting.InvertX", "InvertCameraX", general, "selector", off, on),
                O("setting.InvertY", "InvertCameraY", general, "selector", off, on),
                O("setting.AimAssist", "EnableAimAssist", general, "selector", off, on),
                O("setting.AimBehavior", "AimBehavior", advanced, "selector", "setting.ToggleTxt", "setting.HoldTxt"),
                O("setting.ExitIronSight", "SingleTapExitIronSight", advanced, "selector", "setting.DoubleTap", "setting.SingleTap"),
                O("setting.DoubleTapHoldShoot", "DoubleTapHoldToIronSightFire", advanced, "selector", off, on),
                O("setting.AutoShootOnlyWhileAiming", "AutoShootAlwaysActive", advanced, "selector", on, off),
                O("setting.AutoShootCancelsSprinting", "AutoShootCancelsSprinting", advanced, "selector", off, on),
                O("setting.AutoShootSwitcher", "AutoShootSwitcher", advanced, "selector", off, on),
                O("setting.SprintCancelsReload", "SprintCancelsReload", advanced, "selector", off, on),
                O("setting.ReturnToIron", "ReturnToIronSightAfterReload", advanced, "selector", off, on),
                O("setting.Reload", "Reload", gamepad, "button"),
                O("setting.Ability", "UseEquipment", gamepad, "button"),
                O("setting.Aim", "ZoomToggle", gamepad, "button"),
                O("setting.Shoot", "Fire", gamepad, "button"),
                O("setting.Sprint", "Sprint", gamepad, "button"),
                O("setting.SwitchSprint", "SwitchSprint", gamepad, "button"),
            };
        }

        /// <summary>The _arOptions setup() last built (after the CanChange* push and splice).</summary>
        internal List<Option> Options { get; private set; }

        private FlashObject BuildGameplayMenu()
        {
            var menu = new FlashObject(this, "Menu", McvsMenuNames.Gameplay);
            menu.Members["title"] = "setting.GamePlaySetButton";
            var panel = new FlashObject(this, "MovieClip", "mc_panel");
            var scroll = new FlashObject(this, "ScrollPane", "mc_scrollPane");
            var content = new FlashObject(this, "MovieClip", "mc_content");
            content.Members["alpha"] = 1f;
            content.Members["y"] = 0f;
            menu.Add(panel);
            panel.Add(scroll);
            scroll.Add(content);
            scroll.Add(new FlashObject(this, "MovieClip", "mc_masker"));
            scroll.Add(new FlashObject(this, "MovieClip", "mc_bounds"));
            scroll.Members["boundsUpdates"] = 0f;
            scroll.Methods["updateBounds"] = a => { scroll.Members["boundsUpdates"] = scroll.Num("boundsUpdates") + 1; return null; };
            menu.Members["mc_content"] = content;   // public var mc_content (MGS:74)
            menu.Members["mc_scroller"] = scroll;
            return menu;
        }

        /// <summary>mc_content of the Gameplay Settings menu on the stack, or null.</summary>
        public FlashObject Content
        {
            get
            {
                for (int i = Stack.Count - 1; i >= 0; i--)
                {
                    if (Stack[i].Name == McvsMenuNames.Gameplay) return Stack[i].Get("mc_content") as FlashObject;
                }
                return null;
            }
        }

        /// <summary>The page's ScrollPane.updateBounds() calls (setup() itself makes one on frame 1).</summary>
        public int BoundsUpdates
        {
            get
            {
                FlashObject content = Content;
                return content == null || content.Parent == null ? 0 : (int)content.Parent.Num("boundsUpdates");
            }
        }

        /// <summary>The optionMC rows of mc_content, in child order.</summary>
        public List<FlashObject> Rows()
        {
            FlashObject content = Content;
            return content == null ? new List<FlashObject>() : content.Children.Where(c => c.Kind == "optionMC").ToList();
        }

        /// <summary>The rows' <c>type</c>, in child order.</summary>
        public List<string> RowTypes()
        {
            return Rows().Select(r => r.Get("type") as string).ToList();
        }

        /// <summary>mc_content's children as "category:&lt;id&gt;" and "row:&lt;type&gt;", in child order.</summary>
        public List<string> ContentLayout()
        {
            FlashObject content = Content;
            if (content == null) return new List<string>();
            return content.Children.Select(c => c.Kind == "categoryMC" ? "category:" + c.Child("tf_title").Get("text") : "row:" + c.Get("type")).ToList();
        }

        /// <summary>The first row of that type, or null.</summary>
        public FlashObject Row(string type)
        {
            return Rows().FirstOrDefault(r => (r.Get("type") as string) == type);
        }

        private void EmptyContent(FlashObject menu)
        {
            var content = menu.Get("mc_content") as FlashObject;
            if (content != null) content.Children.Clear();
        }

        /// <summary>MenuGameplaySettings.onFocusIn (MGS:719-726): _isInitialized false, EnterSection, setup(), _isInitialized true.</summary>
        private void FocusInGameplay(FlashObject menu)
        {
            GameplayInitialized = false;
            CallFromFlash("EnterSection", null, "settings_gameplay_screen");
            GameplaySetup(menu);
            GameplayInitialized = true;
        }

        private void GameplaySetup(FlashObject menu)
        {
            List<Option> options = InitOptions();
            if (Truthy(CallFromFlash("CanChangeMaxFramerate", true)))
            {
                var values = new List<object>();
                foreach (object v in Sequence(CallFromFlash("GetMaxFramerateValues", new object[0])))
                {
                    float n = ToFloat(v);
                    values.Add(n <= 0 ? "setting.UncappedFPS" : NumberText(n));
                }
                options.Add(new Option { Title = "setting.FrameRateLimit", Type = "MaxFramerate", Category = "setting.AdvancedOptions", Setup = "selector", Values = values });
            }
            if (Truthy(CallFromFlash("CanChangeGraphicSetting", true)))
            {
                options.Insert(2, O("setting.GraphicSetting", "GraphicSetting", "menu.GeneralTxt", "selector", "setting.Performance", "setting.Default", "setting.Quality"));
            }
            Options = options;
            FlashObject panel = menu.Child("mc_panel");
            panel.Frame = InGame ? 2 : 1;
            FlashObject scroll = panel.Child("mc_scrollPane");
            if (panel.Frame != 2) scroll.Call("updateBounds", new object[0]);
            var content = (FlashObject)menu.Get("mc_content");
            string category = "";
            float y = 0;
            for (int k = 0; k < options.Count; k++)
            {
                Option option = options[k];
                if (option.Category == "setting.CustomizeSettingsGamepad") continue;   // not from the gamepad popup
                if (option.Category != category)
                {
                    category = option.Category;
                    var header = new FlashObject(this, "categoryMC", "instance" + (++instances).ToString(CultureInfo.InvariantCulture));
                    header.Add(new FlashObject(this, "TextField", "tf_title"));
                    header.Members["height"] = CategoryHeight;
                    content.Add(header);
                    SetStringId(header.Child("tf_title"), category, new object[0]);
                    header.Members["y"] = y;
                    y += CategoryHeight + 10;
                }
                FlashObject row = option.Setup == "slider" ? SliderRow(content, option, k) : option.Setup == "selector" ? SelectorRow(content, option, k) : null;
                if (row == null) continue;
                row.Members["y"] = y;
                y += row.Num("height") + 8;
            }
        }

        private FlashObject NewRow(FlashObject content, Option option, int k, string label)
        {
            var row = new FlashObject(this, "optionMC", "item" + k.ToString(CultureInfo.InvariantCulture)) { Label = label };
            row.Members["height"] = RowHeight;
            row.Members["visible"] = true;
            var title = new FlashObject(this, "MovieClip", "tf_title");
            title.Add(new FlashObject(this, "TextField", "tf"));
            row.Add(title);
            content.Add(row);
            row.Members["type"] = option.Type;
            return row;
        }

        private FlashObject SliderRow(FlashObject content, Option option, int k)
        {
            FlashObject row = NewRow(content, option, k, "slider");
            row.Add(new FlashObject(this, "TextField", "tf_value"));
            FlashObject bar = NewScrollBar();
            row.Add(bar);
            object stored = CallFromFlash("GetUserPreference", null, option.Type);
            float v = FakeNativeTable.IsNumber(stored) ? ToFloat(stored) : 0f;   // "as Number": anything else is 0
            float shown = v;
            if (option.Type == "SprintSensitivity") v = 1 - v;
            if (option.Type == "PlayerFov") v = (v - 60) / 60f;
            bar.Set("value", v == 0 ? 0.0001f : v);   // before the CHANGE listener (MGS:333 vs :343)
            SetStringId(row.Child("tf_value"), "menu.ScorePercent", new object[] { (float)Math.Round(v * 100) });
            if (option.Type == "PlayerFov") SetStringId(row.Child("tf_value"), NumberText(shown), new object[0]);
            bar.Child("slot").Child("mc_mask").Members["scaleX"] = v > 0.99f ? 1f : 0.08f + v * 0.84f;
            bar.OnBarChange = value => OnBarScroll(row, bar, value);
            SetStringId(row.Child("tf_title").Child("tf"), option.Title, new object[0]);
            return row;
        }

        private FlashObject SelectorRow(FlashObject content, Option option, int k)
        {
            FlashObject row = NewRow(content, option, k, "selector");
            FlashObject list = NewList();
            row.Add(list);
            var warning = new FlashObject(this, "MovieClip", "mc_warning");
            row.Add(warning);
            SetStringId(row.Child("tf_title").Child("tf"), option.Title, new object[0]);
            warning.Members["visible"] = false;
            list.OnItemSet = index => OnSetListSelector(row, list, index);
            list.OnChange = index => OnListSelectorChange(row, list, index);
            row.Members["values"] = option.Values == null ? new List<object>() : new List<object>(option.Values);
            list.Set("dataLength", (float)(option.Values == null ? 0 : option.Values.Count));
            list.Set("loop", true);
            object stored = CallFromFlash("GetUserPreference", null, option.Type);
            list.Call("displayList", new[] { stored ?? 0f });
            if (list.ListLength > 2) list.Call("displayList", new[] { CallFromFlash("GetUserPreference", null, option.Type) });
            if (option.Type == "SingleTapExitIronSight")
            {
                object mode = CallFromFlash("GetUserPreference", null, "IronSightMode");
                row.Members["visible"] = !(FakeNativeTable.IsNumber(mode) && ToFloat(mode) == 1);
            }
            if (option.Type == "MaxFramerate") warning.Members["visible"] = true;
            return row;
        }

        private FlashObject NewList()
        {
            var list = new FlashObject(this, "List", "mc_listSelector");
            list.Members["loop"] = false;
            list.Getters["value"] = () => (float)list.ListIndex;
            list.Setters["value"] = v => list.DisplayList(ToIndex(v));
            list.Getters["dataLength"] = () => (float)list.ListLength;
            list.Setters["dataLength"] = v =>
            {
                list.ListLength = Math.Max(0, ToIndex(v));
                if (list.ListIndex > list.ListLength - 1 && list.ListIndex > 0) list.DisplayList(list.ListLength);
                if (list.ListLength <= 1) list.ListIndex = 0;
            };
            list.Methods["displayList"] = a => { list.DisplayList(ToIndex(a.Length > 0 ? a[0] : null)); return null; };
            return list;
        }

        private FlashObject NewScrollBar()
        {
            var bar = new FlashObject(this, "ScrollBar", "mc_scrollbar");
            var slot = new FlashObject(this, "MovieClip", "slot");
            var mask = new FlashObject(this, "MovieClip", "mc_mask");
            mask.Members["scaleX"] = 0.08f;
            slot.Add(mask);
            bar.Add(slot);
            bar.Add(new FlashObject(this, "MovieClip", "btn_handle"));
            bar.Getters["value"] = () => bar.BarValue;
            bar.Setters["value"] = v => bar.SetBarValue(ToFloat(v));
            return bar;
        }

        /// <summary>onSetListSelector (ITEM_SET, MGS:517-521): the shown text is the row's <c>values[index]</c> at this moment.</summary>
        private void OnSetListSelector(FlashObject row, FlashObject list, int index)
        {
            var values = row.Get("values") as IList;
            object text = values != null && index >= 0 && index < values.Count ? values[index] : "undefined";
            SetStringId(list, text, new object[0]);
        }

        /// <summary>onListSelectorChange (MGS:523-576).</summary>
        private void OnListSelectorChange(FlashObject row, FlashObject list, int index)
        {
            string type = row.Get("type") as string;
            if (type == "AimBehavior" || type == "AbilityButtonPlacement")
            {
                if (GameplayInitialized) CallFromFlash("SetUserPreference", null, type, index);
            }
            else if (type == "IronSightMode")
            {
                if (GameplayInitialized) CallFromFlash("SetUserPreference", null, type, index);
                int exit = Options == null ? -1 : Options.FindIndex(o => o.Type == "SingleTapExitIronSight");
                FlashObject exitRow = exit < 0 || Content == null ? null : Content.Child("item" + exit.ToString(CultureInfo.InvariantCulture));
                if (exitRow != null) exitRow.Members["visible"] = index != 1;
            }
            else if (type == "MaxFramerate")
            {
                if (GameplayInitialized)
                {
                    CallFromFlash("SetUserPreference", null, type, index);
                    if (index > 0) FramerateWarnings++;
                }
            }
            else if (type == "GraphicSetting")
            {
                if (GameplayInitialized)
                {
                    CallFromFlash("SetUserPreference", null, type, index);
                    CallFromFlash("ChangeGraphicSetting", null, index);
                }
            }
            else if (GameplayInitialized)
            {
                CallFromFlash("SetUserPreference", null, type, index != 0);
            }
            CallFromFlash("SaveUserPreferences", null);
        }

        /// <summary>onBarScroll (MGS:487-509): no _isInitialized guard.</summary>
        private void OnBarScroll(FlashObject row, FlashObject bar, float value)
        {
            if (value > 0.99f)
            {
                value = 1;
                bar.Child("slot").Child("mc_mask").Members["scaleX"] = 1f;
            }
            else
            {
                bar.Child("slot").Child("mc_mask").Members["scaleX"] = 0.08f + value * 0.84f;
            }
            SetStringId(row.Child("tf_value"), "menu.ScorePercent", new object[] { (float)Math.Round(value * 100) });
            string type = row.Get("type") as string;
            if (type == "SprintSensitivity") value = 1 - value;
            if (type == "PlayerFov")
            {
                value = 60 + (float)Math.Round(value * 60);
                SetStringId(row.Child("tf_value"), NumberText(value), new object[0]);
            }
            CallFromFlash("SetUserPreference", null, type, value);
        }

        /// <summary>The user moves a slider (the handle drag fires CHANGE through ScrollBar.value=).</summary>
        public void Drag(FlashObject row, float value)
        {
            row.Child("mc_scrollbar").Set("value", value);
        }

        /// <summary>The user steps a selector to <paramref name="index"/> (List.displayList through the arrows).</summary>
        public void Select(FlashObject row, int index)
        {
            row.Child("mc_listSelector").Call("displayList", new object[] { (float)index });
        }

        /// <summary>The 'i' button of the FRAME RATE LIMIT row (mc_warning TAP → onShowFramerateWarning).</summary>
        public bool TapWarning(FlashObject row)
        {
            FlashObject warning = row.Child("mc_warning");
            if (warning == null || !Truthy(warning.Get("visible")) || (row.Get("type") as string) != "MaxFramerate") return false;
            FramerateWarnings++;
            return true;
        }

        // ------------------------------------------------------------------ TextUtilities and conversions

        /// <summary>TextUtilities.setStringID(obj, id, params): the text, then Native.GetPlatformId (TextUtilities.as:92-99).</summary>
        internal void SetStringId(FlashObject target, object id, object[] parameters)
        {
            if (target == null) return;
            string text = id == null ? "null" : id is string ? (string)id : FormatAs(id);
            if (parameters != null && parameters.Length > 0) text += " " + string.Join(" ", parameters.Select(FormatAs));
            target.Members["text"] = text;
            CallFromFlash("GetPlatformId", Game.PlatformId);
        }

        internal T Track<T>(T wrapper) where T : IFlashWrapper
        {
            Handed.Add(wrapper);
            return wrapper;
        }

        /// <summary>Every wrapper handed to the host was disposed exactly once.</summary>
        public void AssertAllDisposed()
        {
            foreach (IFlashWrapper w in Handed) Assert.True(w.Disposals == 1, w + " disposed " + w.Disposals + " times");
        }

        internal static bool Truthy(object value)
        {
            if (value == null) return false;
            if (value is bool) return (bool)value;
            if (FakeNativeTable.IsNumber(value)) return ToFloat(value) != 0;
            if (value is string) return ((string)value).Length > 0;
            return true;
        }

        internal static float ToFloat(object value)
        {
            if (value == null) return 0;
            if (value is bool) return (bool)value ? 1 : 0;
            if (FakeNativeTable.IsNumber(value)) return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            float parsed;
            return value is string && float.TryParse((string)value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        /// <summary>AS int(value): Numbers truncate, Boolean is 0/1, null and anything else 0.</summary>
        internal static int ToIndex(object value)
        {
            float f = ToFloat(value);
            return float.IsNaN(f) || float.IsInfinity(f) ? 0 : (int)f;
        }

        private static IEnumerable<object> Sequence(object value)
        {
            var items = value as IEnumerable;
            if (items == null || value is string) yield break;
            foreach (object item in items) yield return item;
        }

        internal static string NumberText(float value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        internal static string FormatAs(object value)
        {
            if (value == null) return "null";
            if (value is bool) return (bool)value ? "true" : "false";
            if (FakeNativeTable.IsNumber(value)) return NumberText(ToFloat(value));
            var obj = value as FlashObject;
            if (obj != null) return "[" + obj.Path + "]";
            var list = value as IList;
            if (list != null) return "[" + string.Join(",", list.Cast<object>().Select(FormatAs)) + "]";
            var dict = value as IDictionary<string, object>;
            if (dict != null) return "{" + string.Join(",", dict.Select(kv => kv.Key + ":" + FormatAs(kv.Value))) + "}";
            return value.ToString();
        }
    }

    /// <summary>The two menu names (SettingsMenuNames has them for the host; the fakes keep their own copy).</summary>
    internal static class McvsMenuNames
    {
        public const string Settings = "menu_settings";
        public const string Gameplay = "menu_gameplaySettings";
    }

    /// <summary>One ShowGenericConfirmPopup call.</summary>
    internal sealed class FakePopup
    {
        private readonly Action<Dictionary<string, object>> callback;

        public FakePopup(string eventName, Dictionary<string, object> data, Action<Dictionary<string, object>> callback, FakePopupPriority priority)
        {
            EventName = eventName;
            Data = data;
            this.callback = callback;
            Priority = priority;
        }

        public string EventName { get; private set; }

        public Dictionary<string, object> Data { get; private set; }

        public FakePopupPriority Priority { get; private set; }

        public bool HasCallback { get { return callback != null; } }

        /// <summary>The player taps Yes (true) or No: the callback gets <c>{"result": yes}</c> (GamePreferencesComponent.cs:220-226).</summary>
        public void Answer(bool yes)
        {
            if (callback != null) callback(new Dictionary<string, object>(StringComparer.Ordinal) { { "result", yes } });
        }
    }

    /// <summary>A double or long the host wrote: ASValue.SetObject stores it as a managed object, not as an AS Number.</summary>
    internal sealed class FlashManaged
    {
        public FlashManaged(object value)
        {
            Value = value;
        }

        public object Value { get; private set; }

        public override string ToString()
        {
            return "managed " + Convert.ToString(Value, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// A display object (or AS object) of the fake Flash VM: named children in depth order, members (numbers as float), computed
    /// members, methods. The simulated ActionScript uses it directly; the host reaches it only through <see cref="FlashCharacter"/>.
    /// </summary>
    internal sealed class FlashObject
    {
        public FlashObject(FakeFlashTree tree, string kind, string name)
        {
            Tree = tree;
            Kind = kind;
            Members["name"] = name;
            Members["visible"] = true;
            Members["mouseEnabled"] = true;
            Members["alpha"] = 1f;
            Getters["numChildren"] = () => (float)Children.Count;
            Getters["currentFrame"] = () => (float)Frame;
        }

        public FakeFlashTree Tree { get; private set; }

        /// <summary>BtnSetting, ThinBtn, optionMC, categoryMC, List, ScrollBar, ScrollPane, TextField, MovieClip, Menu.</summary>
        public string Kind { get; private set; }

        public FlashObject Parent { get; private set; }

        public List<FlashObject> Children { get; } = new List<FlashObject>();

        public Dictionary<string, object> Members { get; } = new Dictionary<string, object>(StringComparer.Ordinal);

        public Dictionary<string, Func<object>> Getters { get; } = new Dictionary<string, Func<object>>(StringComparer.Ordinal);

        public Dictionary<string, Action<object>> Setters { get; } = new Dictionary<string, Action<object>>(StringComparer.Ordinal);

        public Dictionary<string, Func<object[], object>> Methods { get; } = new Dictionary<string, Func<object[], object>>(StringComparer.Ordinal);

        /// <summary>currentFrame (MovieClip).</summary>
        public int Frame { get; set; } = 1;

        /// <summary>The optionMC frame label (slider, selector, button).</summary>
        public string Label { get; set; }

        /// <summary>Every access through a handle throws (a native call that fails).</summary>
        public bool Throws { get; set; }

        public string Name
        {
            get
            {
                object name;
                return Members.TryGetValue("name", out name) ? name as string : null;
            }
            set { Members["name"] = value; }
        }

        public string Path { get { return Parent == null ? Name : Parent.Path + "." + Name; } }

        public FlashObject Child(string name)
        {
            return Children.FirstOrDefault(c => c.Name == name);
        }

        public FlashObject Add(FlashObject child)
        {
            if (child.Parent != null) child.Parent.Children.Remove(child);
            child.Parent = this;
            Children.Add(child);
            return child;
        }

        public float Num(string member)
        {
            return FakeFlashTree.ToFloat(Get(member));
        }

        /// <summary>AS read: a computed member, a member, a named child (a timeline instance is a public var), else null.</summary>
        public object Get(string member)
        {
            Func<object> getter;
            if (Getters.TryGetValue(member, out getter)) return getter();
            object value;
            if (Members.TryGetValue(member, out value)) return value;
            return Child(member);
        }

        /// <summary>AS assignment (numbers stored as float; a FlashManaged stays as it is).</summary>
        public void Set(string member, object value)
        {
            if (FakeNativeTable.IsNumber(value)) value = Convert.ToSingle(value, CultureInfo.InvariantCulture);
            Action<object> setter;
            if (Setters.TryGetValue(member, out setter))
            {
                setter(value);
                return;
            }
            if (member == "name" && value is string) Members["name"] = value;
            else Members[member] = value;
        }

        /// <summary>An AS method: our own, else getChildByName, getChildAt, removeChild, addChild, gotoAndStop.</summary>
        public object Call(string method, object[] args)
        {
            args = args ?? new object[0];
            Func<object[], object> body;
            if (Methods.TryGetValue(method, out body)) return body(args);
            switch (method)
            {
                case "getChildByName":
                    return Child(args.Length > 0 ? args[0] as string : null);
                case "getChildAt":
                {
                    int index = FakeFlashTree.ToIndex(args.Length > 0 ? args[0] : null);
                    if (index < 0 || index >= Children.Count) throw new ArgumentOutOfRangeException("index", "RangeError: Error #2006");
                    return Children[index];
                }
                case "removeChild":
                {
                    var child = args.Length > 0 ? args[0] as FlashObject : null;
                    if (child == null || child.Parent != this) throw new ArgumentException("ArgumentError: Error #2025");
                    Children.Remove(child);
                    child.Parent = null;
                    return child;
                }
                case "addChild":
                {
                    var child = args.Length > 0 ? args[0] as FlashObject : null;
                    if (child != null) Add(child);
                    return child;
                }
                case "gotoAndStop":
                    if (args.Length > 0 && FakeNativeTable.IsNumber(args[0])) Frame = FakeFlashTree.ToIndex(args[0]);
                    else if (args.Length > 0) Label = args[0] as string;
                    return null;
            }
            throw new MissingMemberException(Kind, method);
        }

        // ------------------------------------------------------------------ gluic List and ScrollBar state

        /// <summary>List._firstElementDataIndex (-1: UNDEFINED_INDEX, List.as:98).</summary>
        internal int ListIndex = -1;

        internal int ListLength;
        internal Action<int> OnItemSet;
        internal Action<int> OnChange;
        internal float BarValue;
        internal Action<float> OnBarChange;
        private bool altering;

        /// <summary>List.displayList (List.as:1306-1366, one item, indexIncrements 1): wrap or clamp, ITEM_SET, then CHANGE when it differs.</summary>
        internal void DisplayList(int index)
        {
            bool loop = FakeFlashTree.Truthy(Get("loop"));
            if (!loop)
            {
                if (index < 0 || ListLength <= 1) index = 0;
                else if (index > ListLength - 1) index = ListLength - 1;
            }
            else
            {
                if (index < 0) index = ListLength - 1;
                else if (index >= ListLength) index -= ListLength;
            }
            int old = ListIndex;
            ListIndex = index;
            if (OnItemSet != null) OnItemSet(index);
            if (old != index) Change(() => { if (OnChange != null) OnChange(index); });
        }

        /// <summary>ScrollBar.value= (ScrollBar.as:336-353): clamped to 0..1, CHANGE when it differs; ignored while a change is dispatched.</summary>
        internal void SetBarValue(float value)
        {
            if (altering) return;
            float old = BarValue;
            BarValue = Math.Max(0, Math.Min(1, value));
            float now = BarValue;
            if (old != now) Change(() => { if (OnBarChange != null) OnBarChange(now); });
        }

        private void Change(Action dispatch)
        {
            altering = true;
            try
            {
                dispatch();
            }
            finally
            {
                altering = false;
            }
        }

        public override string ToString()
        {
            return Kind + " " + Path;
        }
    }

    internal interface IFlashWrapper
    {
        int Disposals { get; }
    }

    /// <summary>Gameloft.Engine.ASCharacter look-alike: a new handle per read over the same display object; IDisposable.</summary>
    internal sealed class FlashCharacter : IDisposable, IFlashWrapper
    {
        private readonly FlashObject target;

        public FlashCharacter(FlashObject target)
        {
            this.target = target;
        }

        public FlashObject Target { get { return target; } }

        public int Disposals { get; private set; }

        /// <summary>ASCharacter.Name (the native instance name).</summary>
        public string Name { get { return target.Name ?? ""; } }

        /// <summary>
        /// ASCharacter.CurrentFrame: 0-based, unlike the AS <c>currentFrame</c> (libnativeengine's AS getter adds one to the same
        /// vtable call; the game does <c>GotoAndStop(GetMember("currentFrame") - 1)</c>).
        /// </summary>
        public int CurrentFrame
        {
            get
            {
                Check();
                return target.Frame - 1;
            }
        }

        public FlashValue InvokeMethod(string methodName, params object[] args)
        {
            Check();
            object[] converted = (args ?? new object[0]).Select(a => FlashValue.In(target.Tree, a)).ToArray();
            if (methodName != "getChildByName" && methodName != "getChildAt")
                target.Tree.HostWrites.Add(target.Path + "." + methodName + "(" + string.Join(", ", converted.Select(FakeFlashTree.FormatAs)) + ")");
            return target.Tree.Track(new FlashValue(target.Tree, target.Call(methodName, converted)));
        }

        public T InvokeMethod<T>(string methodName, params object[] args)
        {
            target.Tree.GenericCalls++;
            throw new InvalidOperationException("generic overload");
        }

        public FlashValue GetMember(string name)
        {
            Check();
            return target.Tree.Track(new FlashValue(target.Tree, target.Get(name)));
        }

        public T GetMember<T>(string name)
        {
            target.Tree.GenericCalls++;
            throw new InvalidOperationException("generic overload");
        }

        public void SetMember(string name, object value)
        {
            Check();
            object converted = FlashValue.In(target.Tree, value);
            if (converted is FlashManaged) target.Tree.ManagedWrites.Add(target.Path + "." + name);
            target.Tree.HostWrites.Add(target.Path + "." + name + "=" + FakeFlashTree.FormatAs(converted));
            target.Set(name, converted);
        }

        private void Check()
        {
            Assert.True(Disposals == 0, "ASCharacter " + target.Path + " used after Dispose");
            if (target.Throws) throw new InvalidOperationException("probe: native call failed");
        }

        public void Dispose()
        {
            Disposals++;
        }

        public override string ToString()
        {
            return "ASCharacter " + target.Path;
        }
    }

    /// <summary>Gameloft.Engine.ASClass look-alike: an AS class's static functions; IDisposable.</summary>
    internal sealed class FlashClass : IDisposable, IFlashWrapper
    {
        private readonly FakeFlashTree tree;
        private readonly string fullName;

        public FlashClass(FakeFlashTree tree, string fullName)
        {
            this.tree = tree;
            this.fullName = fullName;
        }

        public int Disposals { get; private set; }

        public FlashValue InvokeMethod(string methodName, params object[] args)
        {
            Assert.True(Disposals == 0, "ASClass " + fullName + " used after Dispose");
            object[] converted = (args ?? new object[0]).Select(a => FlashValue.In(tree, a)).ToArray();
            if (methodName != "getMenuOnTopOfStack" && !methodName.StartsWith("getViewport", StringComparison.Ordinal))
                tree.HostWrites.Add(fullName + "." + methodName + "(" + string.Join(", ", converted.Select(FakeFlashTree.FormatAs)) + ")");
            return tree.Track(new FlashValue(tree, tree.CallStatic(fullName, methodName, converted)));
        }

        public T InvokeMethod<T>(string methodName, params object[] args)
        {
            tree.GenericCalls++;
            throw new InvalidOperationException("generic overload");
        }

        public void Dispose()
        {
            Disposals++;
        }

        public override string ToString()
        {
            return "ASClass " + fullName;
        }
    }

    /// <summary>Gameloft.Engine.ASValue look-alike: GetObject(Type) by the value's own type; IDisposable.</summary>
    internal sealed class FlashValue : IDisposable, IFlashWrapper
    {
        private readonly FakeFlashTree tree;
        private readonly object value;

        public FlashValue(FakeFlashTree tree, object value)
        {
            this.tree = tree;
            this.value = value;
        }

        public int Disposals { get; private set; }

        /// <summary>ASValue.GetObject(null): Number → float, Boolean, String, Null, Character → a new ASCharacter, Array → List, object → Dictionary.</summary>
        public object GetObject(Type type)
        {
            Assert.Null(type);
            Assert.True(Disposals == 0, "ASValue used after Dispose");
            return Out(tree, value);
        }

        public object GetObject(object typeOrTypes)
        {
            throw new InvalidOperationException("the Type overload is the one to use");
        }

        public void Dispose()
        {
            Disposals++;
        }

        /// <summary>A C# value as ASValue.SetObject passes it into AS.</summary>
        internal static object In(FakeFlashTree tree, object value)
        {
            if (value == null || value is string || value is bool) return value;
            var character = value as FlashCharacter;
            if (character != null) return character.Target;
            if (value is int || value is uint || value is float || value is short || value is Enum)
                return Convert.ToSingle(value is Enum ? (object)Convert.ToInt32(value, CultureInfo.InvariantCulture) : value, CultureInfo.InvariantCulture);
            if (value is double || value is long) return new FlashManaged(value);
            var dict = value as IDictionary;
            if (dict != null)
            {
                var copy = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry e in dict) copy[Convert.ToString(e.Key, CultureInfo.InvariantCulture)] = In(tree, e.Value);
                return copy;
            }
            var list = value as IList;
            if (list != null) return list.Cast<object>().Select(o => In(tree, o)).ToList();
            return new FlashManaged(value);
        }

        internal static object Out(FakeFlashTree tree, object value)
        {
            if (value == null || value is string || value is bool || value is float) return value;
            if (FakeNativeTable.IsNumber(value)) return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            var obj = value as FlashObject;
            if (obj != null) return tree.Track(new FlashCharacter(obj));
            var managed = value as FlashManaged;
            if (managed != null) return managed.Value;
            var dict = value as IDictionary<string, object>;
            if (dict != null) return dict.ToDictionary(kv => kv.Key, kv => Out(tree, kv.Value), StringComparer.Ordinal);
            var list = value as IList;
            if (list != null) return list.Cast<object>().Select(o => Out(tree, o)).ToList();
            return value;
        }
    }
}
