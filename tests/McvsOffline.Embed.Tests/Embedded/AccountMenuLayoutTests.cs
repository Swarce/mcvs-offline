using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Admin;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// Settings › Account with admin.accountMenu.hideDeadButtons (design §9.2): the filter in AdminHooks.AccountMenuButtons on
    /// look-alikes of the game's anonymous entries (PlayerProfileComponent.GetAccountMenuButtonList), and the centring step
    /// (AccountMenuLayout) on look-alikes of the engine's Flash interop (UIComponent, ASClass, ASCharacter, ASValue).
    /// </summary>
    [Collection("GameHooks")]
    public class AccountMenuLayoutTests
    {
        private const string HideKey = "admin.accountMenu.hideDeadButtons";

        // The game's own entries, built as PlayerProfileComponent.cs:4526-4595 builds them (anonymous types).
        private static object Facebook() => new { snsId = 0, isClickable = true, icon = "Menus/assets/png/icons/facebook.png", label = "setting.FacebookButton" };

        private static object Redeem() => new { customAction = "showRedeem", isClickable = true, label = "setting.RedeemCodeButton", icon = "Menus/assets/png/icons/redeemCode.png" };

        private static object Google() => new { snsId = 2, isClickable = true, icon = "Menus/assets/png/icons/googlePlus.png", label = "setting.GoogleButton" };

        private static object GameCenter() => new { snsId = 1, isClickable = false, icon = "Menus/assets/png/icons/gamecenter.png", label = "setting.GameCenterButton" };

        private static object Restore() => new { customAction = "restore_purchase", label = "Social.RecoverProgressButton" };

        private static string Describe(object entry)
        {
            var ours = entry as Dictionary<string, object>;
            if (ours != null) return "ours:" + ours["customAction"];
            object sns = AccountMenuLayout.Member(entry, "snsId");
            return sns != null ? "sns:" + sns : "action:" + AccountMenuLayout.Member(entry, "customAction");
        }

        private static string[] Names(IEnumerable<object> list) => list.Select(Describe).ToArray();

        [Theory]
        // setting, portal, redeem → the list Flash gets, and how many buttons the posted step centres (0: none posted).
        [InlineData(true, true, true, "action:showRedeem,ours:mcvs_offline", 2)]
        [InlineData(true, false, true, "action:showRedeem", 1)]
        [InlineData(true, true, false, "ours:mcvs_offline", 1)]
        [InlineData(true, false, false, "sns:0,sns:2,action:restore_purchase", 0)]   // never an empty menu: the game's list
        [InlineData(false, true, true, "sns:0,action:showRedeem,sns:2,action:restore_purchase,ours:mcvs_offline", 0)]
        [InlineData(false, false, true, "sns:0,action:showRedeem,sns:2,action:restore_purchase", 0)]
        [InlineData(false, true, false, "sns:0,sns:2,action:restore_purchase,ours:mcvs_offline", 0)]
        [InlineData(false, false, false, "sns:0,sns:2,action:restore_purchase", 0)]
        public void TheAndroidListKeepsRedeemAndOurs(bool hide, bool portal, bool redeem, string expected, int centred)
        {
            using (var env = new MenuEnvironment("acct-filter"))
            {
                env.Server.Context.Settings.Set(HideKey, hide);
                env.Server.Context.Settings.Set("admin.enabled", portal);
                var game = new List<object> { Facebook() };
                if (redeem) game.Add(Redeem());
                game.Add(Google());
                game.Add(Restore());
                object redeemEntry = redeem ? game[1] : null;
                for (int i = 0; i < centred; i++) env.Ui.Menu.AddButton("btnSocial_customAction" + i, 77, 320 + i * 177);

                IEnumerable<object> result = AdminHooks.AccountMenuButtons(game);

                Assert.Same(game, result);   // the game's own List<object>, changed in place
                Assert.Equal(expected, string.Join(",", Names(game)));
                if (redeem) Assert.Contains(game, e => ReferenceEquals(e, redeemEntry));   // the game's object itself
                Assert.Equal(centred == 0 ? 0 : 1, env.Game.App.Context.Pending);
                env.Game.App.Context.Drain();
                if (centred > 0) Assert.True(env.Logs.Contains("account menu: " + centred + (centred == 1 ? " button" : " buttons") + " centred"));
                else Assert.DoesNotContain(env.Logs.Snapshot(), e => e.Message.StartsWith("account menu:", StringComparison.Ordinal));
            }
        }

        [Fact]
        public void TheRealWin32ListOnlyGainsTheMcvsButton()
        {
            using (var env = new MenuEnvironment("acct-win32"))
            {
                // The Steam Game.dll's GetAccountMenuButtonList on Win32: Redeem, then GameCenter (snsId 1); no Facebook,
                // no Google, no Restore (iOS only) and no link-code button (platforms other than Android, iOS and Win32).
                var game = new List<object> { Redeem(), GameCenter() };
                AdminHooks.AccountMenuButtons(game);
                Assert.Equal(new[] { "action:showRedeem", "sns:1", "ours:mcvs_offline" }, game.Select(Describe).ToArray());
                Assert.Equal(0, env.Game.App.Context.Pending);   // an snsId entry: nothing to centre, nothing posted
            }
        }

        [Fact]
        public void ThePcListKeepsGameCenterAndUnknownEntries()
        {
            using (var env = new MenuEnvironment("acct-pc"))
            {
                // Robustness, not a real list: Redeem, GameCenter (snsId 1), the link-code button of platforms other than
                // Android, iOS and Win32, and Restore (iOS only); plus things the filter cannot read.
                object linkCode = new { customAction = "ShowLinkCode", isClickable = true, label = "setting.LinkCode" };
                object oddSns = new { snsId = "0", label = "a string snsId" };
                object throwing = new ThrowingEntry();
                var game = new List<object> { Redeem(), GameCenter(), linkCode, null, oddSns, throwing, Restore() };
                AdminHooks.AccountMenuButtons(game);
                Assert.Equal(7, game.Count);
                Assert.Equal("action:showRedeem", Describe(game[0]));
                Assert.Equal("sns:1", Describe(game[1]));
                Assert.Same(linkCode, game[2]);
                Assert.Null(game[3]);
                Assert.Same(oddSns, game[4]);
                Assert.Same(throwing, game[5]);
                Assert.Equal("ours:mcvs_offline", Describe(game[6]));
                Assert.Equal(0, env.Game.App.Context.Pending);   // more than two buttons and an snsId: the game's layout stays

                // The three dead entries, also when they come as a dictionary or with an enum snsId.
                Assert.True(AccountMenuLayout.IsDeadEntry(new Dictionary<string, object> { { "snsId", 2 } }));
                Assert.True(AccountMenuLayout.IsDeadEntry(new { snsId = DayOfWeek.Sunday }));
                Assert.True(AccountMenuLayout.IsDeadEntry(new { snsId = 2L }));
                Assert.False(AccountMenuLayout.IsDeadEntry(new { snsId = 5 }));
                Assert.False(AccountMenuLayout.IsDeadEntry(new { customAction = "showRedeem" }));
                Assert.False(AccountMenuLayout.IsDeadEntry(new { customAction = "9splay_migrate" }));
                Assert.False(AccountMenuLayout.IsDeadEntry("text"));
                Assert.False(AccountMenuLayout.IsDeadEntry(new ThrowingEntry()));

                // Centring needs one or two custom-action buttons.
                Assert.True(AccountMenuLayout.CanCentre(new List<object> { Redeem() }));
                Assert.True(AccountMenuLayout.CanCentre(new List<object> { Redeem(), new Dictionary<string, object> { { "customAction", "mcvs_offline" } } }));
                Assert.False(AccountMenuLayout.CanCentre(new List<object> { Redeem(), GameCenter() }));
                Assert.False(AccountMenuLayout.CanCentre(new List<object> { Redeem(), Redeem(), Redeem() }));
                Assert.False(AccountMenuLayout.CanCentre(new List<object>()));
                Assert.False(AccountMenuLayout.CanCentre(new List<object> { null }));
                Assert.False(AccountMenuLayout.CanCentre(null));
            }
        }

        [Fact]
        public void WithoutTheServerTheListIsTheGames()
        {
            AdminHooks.Host = null;
            var game = new List<object> { Facebook(), Redeem(), Google(), Restore() };
            Assert.Same(game, AdminHooks.AccountMenuButtons(game));
            Assert.Equal(4, game.Count);

            // No admin module: the filter still runs (the setting is the server's), no MCVS entry.
            using (var env = new MenuEnvironment("acct-noportal", admin: false))
            {
                Assert.Same(game, AdminHooks.AccountMenuButtons(game));
                Assert.Equal(new[] { "action:showRedeem" }, Names(game));
                Assert.Equal(1, env.Game.App.Context.Pending);
            }
        }

        [Fact]
        public void PlanCentresInTheViewport()
        {
            // S22 (layout.verify.md §2): viewport 0..2048, both symbols 841 wide.
            Assert.True(AccountMenuLayout.Plan(0, 2048, 841, 841, out double x0, out double x1));
            Assert.Equal(158, x0);
            Assert.Equal(1049, x1);
            Assert.Equal(1024, (x0 + x1 + 841) / 2);   // the pair's centre is the viewport's

            Assert.True(AccountMenuLayout.Plan(0, 2048, 841, null, out x0, out x1));
            Assert.Equal(603.5, x0);
            Assert.True(double.IsNaN(x1));

            // A viewport that does not start at 0 (2236 wide, starting at -94): still centred in it.
            Assert.True(AccountMenuLayout.Plan(-94, 2236, 841, 841, out x0, out x1));
            Assert.Equal(158, x0);
            Assert.Equal(1049, x1);

            // Different widths: the gap stays 50.
            Assert.True(AccountMenuLayout.Plan(0, 2048, 800, 900, out x0, out x1));
            Assert.Equal(149, x0);
            Assert.Equal(999, x1);

            // Does not fit, or nonsense: no plan.
            Assert.False(AccountMenuLayout.Plan(0, 1700, 841, 841, out x0, out x1));
            Assert.True(double.IsNaN(x0));
            Assert.True(AccountMenuLayout.Plan(0, 1732, 841, 841, out x0, out x1));   // exactly fits
            Assert.False(AccountMenuLayout.Plan(0, 0, 841, null, out x0, out x1));
            Assert.False(AccountMenuLayout.Plan(0, 2048, 0, null, out x0, out x1));
            Assert.False(AccountMenuLayout.Plan(0, 2048, 841, -1, out x0, out x1));
            Assert.False(AccountMenuLayout.Plan(double.NaN, 2048, 841, 841, out x0, out x1));
            Assert.False(AccountMenuLayout.Plan(0, double.PositiveInfinity, 841, 841, out x0, out x1));
        }

        [Fact]
        public void TheHookCentresBothButtonsOnTheFirstRow()
        {
            using (var env = new MenuEnvironment("acct-centre"))
            {
                // What onPush built from [Redeem, MCVS Offline]: column 0, rows 0 and 1.
                FakeAsCharacter b0 = env.Ui.Menu.AddButton("btnSocial_customAction0", 77, 320);
                FakeAsCharacter b1 = env.Ui.Menu.AddButton("btnSocial_customAction1", 77, 497);
                var game = new List<object> { Facebook(), Redeem(), Google(), Restore() };
                AdminHooks.AccountMenuButtons(game);
                Assert.Equal(77f, b0.Members["x"]);   // nothing moves inside the native call
                env.Game.App.Context.Drain();

                Assert.IsType<float>(b0.Members["x"]);   // an AS Number (a double would become a managed object)
                Assert.Equal(158f, b0.Members["x"]);
                Assert.Equal(320f, b0.Members["y"]);
                Assert.Equal(1049f, b1.Members["x"]);
                Assert.Equal(320f, b1.Members["y"]);
                Assert.Equal(new[] { "x" }, b0.Sets);
                Assert.Equal(new[] { "x", "y" }, b1.Sets);
                Assert.True(env.Logs.Contains("account menu: 2 buttons centred (vpX=0, vpW=2048, x0=158, x1=1049)"));
                Assert.Single(env.Logs.Snapshot(), e => e.Message.StartsWith("account menu:", StringComparison.Ordinal));
                Assert.Contains(env.Logs.Snapshot(), e => e.Format().Contains("[admin] account menu: 2 buttons centred"));
                env.Ui.AssertAllDisposed();

                // The generic InvokeMethod<T> overloads are never used (they would throw here).
                Assert.Equal(0, FakeAsCharacter.GenericCalls);
            }
        }

        [Fact]
        public void OneButtonIsCentredAlone()
        {
            using (var env = new MenuEnvironment("acct-one"))
            {
                env.Server.Context.Settings.Set("admin.enabled", false);
                FakeAsCharacter b0 = env.Ui.Menu.AddButton("btnSocial_customAction0", 77, 320);
                AdminHooks.AccountMenuButtons(new List<object> { Facebook(), Redeem(), Google(), Restore() });
                env.Game.App.Context.Drain();
                Assert.Equal(603.5f, b0.Members["x"]);
                Assert.Equal(new[] { "x" }, b0.Sets);
                Assert.True(env.Logs.Contains("account menu: 1 button centred (vpX=0, vpW=2048, x0=603.5)"));
                env.Ui.AssertAllDisposed();
            }
        }

        [Theory]
        [InlineData("menu", "the menu on top is menu_settings")]
        [InlineData("button", "no btnSocial_customAction1")]
        [InlineData("width", "btnSocial_customAction1 has no width")]
        [InlineData("viewport", "no VPManager")]
        [InlineData("bounds", "no viewport bounds")]
        [InlineData("narrow", "does not fit (vpX=0, vpW=1600, w0=841, w1=841)")]
        [InlineData("stack", "no MenusStack")]
        [InlineData("empty", "no menu on top")]
        [InlineData("ui", "no UI")]
        [InlineData("throws", "not centred (InvalidOperationException: probe: native call failed)")]
        public void AnyFailureLogsOneLineAndMovesNothing(string fault, string reason)
        {
            using (var env = new MenuEnvironment("acct-fail"))
            {
                FakeAsCharacter b0 = env.Ui.Menu.AddButton("btnSocial_customAction0", 77, 320);
                FakeAsCharacter b1 = env.Ui.Menu.AddButton("btnSocial_customAction1", 77, 497);
                switch (fault)
                {
                    case "menu": env.Ui.Menu.Name = "menu_settings"; break;
                    case "button": env.Ui.Menu.Children.Remove("btnSocial_customAction1"); break;
                    case "width": b1.Members.Remove("width"); break;
                    case "viewport": env.Ui.Classes.Remove("managers.VPManager"); break;
                    case "bounds": env.Ui.Classes["managers.VPManager"].Statics.Remove("getViewportBoundWidth"); break;
                    case "narrow": env.Ui.Classes["managers.VPManager"].Statics["getViewportBoundWidth"] = 1600f; break;
                    case "stack": env.Ui.Classes.Remove("gluic.components.menus.MenusStack"); break;
                    case "empty": env.Ui.Classes["gluic.components.menus.MenusStack"].Statics.Remove("getMenuOnTopOfStack"); break;
                    case "ui": FakeUiExtensions.Ui = null; break;
                    case "throws": b1.Throw = true; break;
                }
                AdminHooks.AccountMenuButtons(new List<object> { Redeem() });
                env.Game.App.Context.Drain();
                Assert.Empty(b0.Sets);
                Assert.Empty(b1.Sets);
                LogEntry line = Assert.Single(env.Logs.Snapshot(), e => e.Message.StartsWith("account menu:", StringComparison.Ordinal));
                Assert.Contains(reason, line.Message);
                Assert.StartsWith("account menu: not centred (", line.Message);
                env.Ui.AssertAllDisposed();
            }
        }

        [Fact]
        public void TheMenuNameMayComeFromFlash()
        {
            using (var env = new MenuEnvironment("acct-asname"))
            {
                // ASCharacter.Name empty, the AS "name" member set (MenusStack.createMenu: menu.name = "menu_accounts").
                env.Ui.Menu.Name = "";
                env.Ui.Menu.Members["name"] = "menu_accounts";
                env.Server.Context.Settings.Set("admin.enabled", false);   // [Redeem] alone
                FakeAsCharacter b0 = env.Ui.Menu.AddButton("btnSocial_customAction0", 77, 320);
                AdminHooks.AccountMenuButtons(new List<object> { Redeem() });
                env.Game.App.Context.Drain();
                Assert.Equal(603.5f, b0.Members["x"]);
            }
        }

        [Fact]
        public void NoGameThreadLogsOneLine()
        {
            using (var env = new MenuEnvironment("acct-nogame"))
            {
                FakeGame.Uninstall();
                var game = new List<object> { Facebook(), Redeem(), Google(), Restore() };
                AdminHooks.AccountMenuButtons(game);
                Assert.Equal(2, game.Count);   // the list is filtered anyway
                Assert.True(env.Logs.Contains("account menu: not centred (no game thread)"));
            }
        }

        /// <summary>A game entry whose properties throw.</summary>
        private sealed class ThrowingEntry
        {
            public object snsId { get { throw new InvalidOperationException("probe"); } }

            public object customAction { get { throw new InvalidOperationException("probe"); } }
        }

        /// <summary>The server, an AdminHost over the fake game whose GameReflection also knows the fake UI; AdminHooks.Host.</summary>
        private sealed class MenuEnvironment : IDisposable
        {
            public MenuEnvironment(string name, bool admin = true)
            {
                Logs = new MemoryLogSink();
                Server = AdminFixtures.CreateServer(TestEnvironment.NewTempDir(name), Logs, admin);
                Game = FakeGame.Install();
                var reflection = new GameReflection();
                Assert.True(reflection.Bind(typeof(FakeGameApplication), typeof(FakeProfile), typeof(FakeUiExtensions)));
                Ui = new FakeUi();
                FakeUiExtensions.Ui = Ui;
                Host = new AdminHost(Server.Context, new RecordingAdminPlatform(), reflection, null);
                Server.Context.Features.Set<IAdminHost>(Host);
                AdminHooks.Host = Host;
            }

            public MemoryLogSink Logs { get; }
            public McvsServer Server { get; }
            public FakeGame Game { get; }
            public FakeUi Ui { get; }
            public AdminHost Host { get; }

            public void Dispose()
            {
                AdminHooks.Host = null;
                FakeUiExtensions.Ui = null;
                FakeGame.Uninstall();
                Server.Dispose();
            }
        }
    }

    // ------------------------------------------------------------------ look-alikes of the engine's Flash interop

    /// <summary>Game.UISceneExtentions: GetUI(World).</summary>
    internal static class FakeUiExtensions
    {
        public static FakeUi Ui;

        public static FakeUi GetUI(FakeWorld world)
        {
            return world == null ? null : Ui;
        }
    }

    /// <summary>Game.UIComponent (a MenuComponent): FindClass(package, class) → a new ASClass wrapper or null.</summary>
    internal sealed class FakeUi
    {
        public readonly Dictionary<string, FakeAsClass> Classes = new Dictionary<string, FakeAsClass>(StringComparer.Ordinal);
        public readonly List<IFakeWrapper> Handed = new List<IFakeWrapper>();

        public FakeUi()
        {
            Menu = new FakeAsCharacter(this) { Name = "menu_accounts" };
            Classes["gluic.components.menus.MenusStack"] = new FakeAsClass(this) { Statics = { { "getMenuOnTopOfStack", Menu } } };
            Classes["managers.VPManager"] = new FakeAsClass(this) { Statics = { { "getViewportBoundX", 0f }, { "getViewportBoundWidth", 2048f } } };
        }

        public FakeAsCharacter Menu { get; }

        public FakeAsClass FindClass(string packageName, string className)
        {
            FakeAsClass found;
            if (!Classes.TryGetValue(packageName + "." + className, out found)) return null;
            return Track(found.Handle());
        }

        public T Track<T>(T wrapper) where T : IFakeWrapper
        {
            Handed.Add(wrapper);
            return wrapper;
        }

        /// <summary>Every wrapper handed to the host was disposed exactly once.</summary>
        public void AssertAllDisposed()
        {
            foreach (IFakeWrapper w in Handed) Assert.True(w.Disposals == 1, w + " disposed " + w.Disposals + " times");
        }
    }

    internal interface IFakeWrapper
    {
        int Disposals { get; }
    }

    /// <summary>Gameloft.Engine.ASValue: GetObject(Type) and GetObject(object); IDisposable.</summary>
    internal sealed class FakeAsValue : IDisposable, IFakeWrapper
    {
        private readonly object value;

        public FakeAsValue(object value)
        {
            this.value = value;
        }

        public int Disposals { get; private set; }

        public object GetObject(Type type)
        {
            Assert.Null(type);   // by the value's own type
            Assert.Equal(0, Disposals);
            var character = value as FakeAsCharacter;
            return character != null ? (object)character.Ui.Track(character.Handle()) : value;
        }

        public object GetObject(object typeOrTypes)
        {
            throw new InvalidOperationException("the Type overload is the one to use");
        }

        public void Dispose()
        {
            Disposals++;
        }
    }

    /// <summary>Gameloft.Engine.ASClass: static AS functions by name; IDisposable.</summary>
    internal sealed class FakeAsClass : IDisposable, IFakeWrapper
    {
        private readonly FakeUi ui;

        public FakeAsClass(FakeUi ui)
        {
            this.ui = ui;
        }

        public Dictionary<string, object> Statics { get; private set; } = new Dictionary<string, object>(StringComparer.Ordinal);

        public int Disposals { get; private set; }

        public FakeAsClass Handle()
        {
            return new FakeAsClass(ui) { Statics = Statics };
        }

        public FakeAsValue InvokeMethod(string methodName, params object[] args)
        {
            Assert.Equal(0, Disposals);
            object result;
            return ui.Track(new FakeAsValue(Statics.TryGetValue(methodName, out result) ? result : null));
        }

        public T InvokeMethod<T>(string methodName, params object[] args)
        {
            FakeAsCharacter.GenericCalls++;
            throw new InvalidOperationException("generic overload");
        }

        public void Dispose()
        {
            Disposals++;
        }
    }

    /// <summary>
    /// Gameloft.Engine.ASCharacter: Name, InvokeMethod("getChildByName", name), GetMember/SetMember; IDisposable. Every handle the
    /// host gets is a new wrapper over the same display object (as GetCharacter() makes), sharing its members and children.
    /// </summary>
    internal sealed class FakeAsCharacter : IDisposable, IFakeWrapper
    {
        public static int GenericCalls;

        private FakeAsCharacter target;

        public FakeAsCharacter(FakeUi ui)
        {
            Ui = ui;
        }

        public FakeUi Ui { get; }

        private FakeAsCharacter Target { get { return target ?? this; } }

        public string Name
        {
            get { return Target.name; }
            set { Target.name = value; }
        }

        private string name;

        public Dictionary<string, object> Members { get { return Target.members; } }

        private readonly Dictionary<string, object> members = new Dictionary<string, object>(StringComparer.Ordinal);

        public Dictionary<string, FakeAsCharacter> Children { get { return Target.children; } }

        private readonly Dictionary<string, FakeAsCharacter> children = new Dictionary<string, FakeAsCharacter>(StringComparer.Ordinal);

        public List<string> Sets { get { return Target.sets; } }

        private readonly List<string> sets = new List<string>();

        public bool Throw
        {
            get { return Target.shouldThrow; }
            set { Target.shouldThrow = value; }
        }

        private bool shouldThrow;

        public int Disposals { get; private set; }

        public FakeAsCharacter Handle()
        {
            return new FakeAsCharacter(Ui) { target = Target };
        }

        public FakeAsCharacter AddButton(string childName, float x, float y)
        {
            var button = new FakeAsCharacter(Ui) { Name = childName };
            button.Members["x"] = x;
            button.Members["y"] = y;
            button.Members["width"] = 841f;
            button.Members["height"] = 132f;
            Children[childName] = button;
            return button;
        }

        public FakeAsValue InvokeMethod(string methodName, params object[] args)
        {
            Assert.Equal(0, Disposals);
            Assert.Equal("getChildByName", methodName);
            FakeAsCharacter child;
            return Ui.Track(new FakeAsValue(Children.TryGetValue((string)args[0], out child) ? child : null));
        }

        public T InvokeMethod<T>(string methodName, params object[] args)
        {
            GenericCalls++;
            throw new InvalidOperationException("generic overload");
        }

        public FakeAsValue GetMember(string member)
        {
            Assert.Equal(0, Disposals);
            if (Throw) throw new InvalidOperationException("probe: native call failed");
            object value;
            return Ui.Track(new FakeAsValue(Members.TryGetValue(member, out value) ? value : null));
        }

        public T GetMember<T>(string member)
        {
            GenericCalls++;
            throw new InvalidOperationException("generic overload");
        }

        public void SetMember(string member, object value)
        {
            Assert.Equal(0, Disposals);
            Members[member] = value;
            Sets.Add(member);
        }

        public void Dispose()
        {
            Disposals++;
        }

        public override string ToString()
        {
            return "ASCharacter " + Name;
        }
    }
}
