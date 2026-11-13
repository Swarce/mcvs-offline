using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using McvsOffline.Embed.Tests.Support;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// Build 32 diagnostics: each profile save (<c>TObject.FromObject</c> of a GameObject, from
    /// <c>GameProfileSection.FromGameObject</c> in <c>PlayerProfileComponent.DoSaveProfile</c>) gets a "profile: save" line with
    /// its World and the game methods that asked for it; a profile text read gets its callers in the "templates:" line. Run on a
    /// fixture assembly named "Game" whose types have the game's names and shapes (async state machines and an async lambda as in
    /// <c>GameplayComponent.OnMatchOverAsync</c>), calling the real <c>GameDataHooks</c> (compiled into the fixture).
    /// </summary>
    [Collection("GameHooks")]
    public class ProfileSaveLineTests
    {
        private const string GameSource = @"
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
namespace Gameloft.Engine
{
    public class ServerComponent { public string ServerType { get; set; } }
    public class World { public bool? IsServer { get; set; } public ServerComponent Server { get; set; } }
    public class GameObject { public World World { get; set; } }
}
namespace Game
{
    public class GameProfileSection
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void FromGameObject(Gameloft.Engine.GameObject go)
        {
            long started = McvsOffline.GameDataHooks.TemplateClock();
            McvsOffline.GameDataHooks.TemplateFromObject(go, started);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void GetTemplate()
        {
            long started = McvsOffline.GameDataHooks.TemplateClock();
            McvsOffline.GameDataHooks.TemplateRead(""DefaultProfile.tx"", started, started, 3, 11368);
        }
    }
    public class ProfileComponent
    {
        public async Task SaveProfile(bool forceSaveIfPaused = false) { await DoSaveProfile(); }
        protected virtual Task DoSaveProfile() { return Task.FromResult(true); }
    }
    public class PlayerProfileComponent : ProfileComponent
    {
        public Gameloft.Engine.GameObject GameObject;
        public GameProfileSection Section = new GameProfileSection();
        protected override async Task DoSaveProfile()
        {
            Section.FromGameObject(GameObject);
            await Task.Yield();
        }
        public override string ToString() { return ""profile""; }
    }
    public class GameplayComponent
    {
        public Task OnMatchOverAsync(PlayerProfileComponent playerProfile)
        {
            Func<Task> func = async delegate
            {
                await playerProfile.SaveProfile();
            };
            return func();
        }
    }
    public class OfflineBotsGameplayComponent
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnControlMessageReceived(PlayerProfileComponent profile)
        {
            profile.SaveProfile(true).Wait();
        }
    }
    public class ProfileManagerComponent
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void LoadProfile(PlayerProfileComponent profile) { profile.Section.GetTemplate(); }
    }
}";

        [Fact]
        public void EachSaveNamesItsWorldAndCallersAndTheOneBefore()
        {
            // The hooks are compiled into the fixture (the test assembly itself holds a GameDataHooks): their own frames are left
            // out by namespace, as on the device by assembly.
            byte[] game = FixtureCompiler.Compile("Game", GameSource, FixtureCompiler.RepoSource("hosts", "embedded", "GameDataHooks.cs"));
            using (var box = new FixtureCompiler.Sandbox().With("Game", game))
            {
                Type hooks = box.Get("Game").GetType("McvsOffline.GameDataHooks", true);
                hooks.GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                var lines = new List<string>();
                hooks.GetMethod("Configure").Invoke(null, new object[] { new Action<string>(l => { lock (lines) lines.Add(l); }) });
                Assembly g = box.Get("Game");

                object Go(bool? isServer, string serverType)
                {
                    object server0 = Activator.CreateInstance(g.GetType("Gameloft.Engine.ServerComponent", true));
                    server0.GetType().GetProperty("ServerType").SetValue(server0, serverType);
                    object world = Activator.CreateInstance(g.GetType("Gameloft.Engine.World", true));
                    world.GetType().GetProperty("IsServer").SetValue(world, isServer);
                    world.GetType().GetProperty("Server").SetValue(world, server0);
                    object go = Activator.CreateInstance(g.GetType("Gameloft.Engine.GameObject", true));
                    go.GetType().GetProperty("World").SetValue(go, world);
                    return go;
                }

                object Profile(object go)
                {
                    object p = Activator.CreateInstance(g.GetType("Game.PlayerProfileComponent", true));
                    p.GetType().GetField("GameObject").SetValue(p, go);
                    return p;
                }

                // The GS's match-over save (an async lambda), then the TS's bot-match save.
                object gs = Profile(Go(true, "gs")), ts = Profile(Go(true, "ts")), client = Profile(Go(false, null));
                object gameplay = Activator.CreateInstance(g.GetType("Game.GameplayComponent", true));
                ((Task)gameplay.GetType().GetMethod("OnMatchOverAsync").Invoke(gameplay, new[] { gs })).Wait();
                object bots = Activator.CreateInstance(g.GetType("Game.OfflineBotsGameplayComponent", true));
                bots.GetType().GetMethod("OnControlMessageReceived").Invoke(bots, new[] { ts });
                ((Task)gameplay.GetType().GetMethod("OnMatchOverAsync").Invoke(gameplay, new[] { client })).Wait();
                object manager = Activator.CreateInstance(g.GetType("Game.ProfileManagerComponent", true));
                manager.GetType().GetMethod("LoadProfile").Invoke(manager, new[] { gs });

                List<string> saves = lines.FindAll(l => l.StartsWith("profile: ", StringComparison.Ordinal));
                Assert.Equal(3, saves.Count);
                const string chain = "GameProfileSection.FromGameObject <- PlayerProfileComponent.DoSaveProfile <- ProfileComponent.SaveProfile <- ";
                Assert.StartsWith("profile: save in the GS World, for " + chain + "GameplayComponent.OnMatchOverAsync; FromObject ", saves[0]);
                Assert.EndsWith("; the first of this start", saves[0]);
                Assert.StartsWith("profile: save in the TS World, for " + chain + "OfflineBotsGameplayComponent.OnControlMessageReceived", saves[1]);
                Assert.Contains(" after the previous one (GS: " + chain + "GameplayComponent.OnMatchOverAsync", saves[1]);
                Assert.StartsWith("profile: save in the client World, for " + chain + "GameplayComponent.OnMatchOverAsync", saves[2]);
                Assert.Contains(" after the previous one (TS: ", saves[2]);
                string read = lines.Find(l => l.StartsWith("templates: DefaultProfile.tx read in ", StringComparison.Ordinal));
                Assert.NotNull(read);
                Assert.EndsWith("(patch 'templates'); for GameProfileSection.GetTemplate <- ProfileManagerComponent.LoadProfile", read);

                // A FromObject of anything else adds no profile line; the lines stop at the limit.
                hooks.GetMethod("TemplateFromObject").Invoke(null, new object[] { new List<int>(), hooks.GetMethod("TemplateClock").Invoke(null, null) });
                int limit = (int)hooks.GetField("ProfileLineLimit").GetValue(null);
                for (int i = 0; i < limit + 5; i++) bots.GetType().GetMethod("OnControlMessageReceived").Invoke(bots, new[] { ts });
                Assert.Equal(limit, lines.FindAll(l => l.StartsWith("profile: ", StringComparison.Ordinal)).Count);
                hooks.GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                hooks.GetMethod("Configure").Invoke(null, new object[] { null });
            }
        }
    }
}
