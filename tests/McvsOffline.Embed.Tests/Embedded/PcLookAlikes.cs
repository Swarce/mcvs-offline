using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using McvsOffline.Embed.Tests.Support;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// Look-alikes of the PC engine members PcHooks uses (docs/design/pc-port.md §8.3, all [V] in PC-EN/): Gameloft.Engine's
    /// ClientComponent, ClientLink, ClientSettings, ServerLocation, LocalAnubisManager (Lobby, Controller, Finder) and
    /// MatchmakingComponent.AutomatchSettings; Gameloft.OnlinePlatform's Session.GetService&lt;T&gt;, MatchmakerService.QuickLaunch
    /// and Room. Each sandbox is a fresh collectible context, so the statics (LocalAnubisManager's) start empty. The fixtures
    /// record the managed thread of every call, so the tests see where PcHooks' continuations run.
    /// </summary>
    internal sealed class PcLookAlikes : IDisposable
    {
        public const string OnlinePlatformSource = @"
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Gameloft.OnlinePlatform
{
    public class Service { public Session Session { get; set; } }

    public class Room
    {
        public string Id { get; set; }
        public string ControllerHost { get; set; }
        public int ControllerPort { get; set; }
    }

    public class Session
    {
        public readonly Dictionary<Type, Service> Services = new Dictionary<Type, Service>();
        public T GetService<T>() where T : Service
        {
            Service s;
            if (!Services.TryGetValue(typeof(T), out s)) { s = (Service)Activator.CreateInstance(typeof(T)); s.Session = this; Services[typeof(T)] = s; }
            return (T)s;
        }
    }

    public class MatchmakerService : Service
    {
        public static readonly List<string> Calls = new List<string>();
        public static Func<string, CancellationToken, string, int?, Task<Room>> Behaviour =
            (type, ct, host, port) => Task.FromResult(new Room { Id = ""room-1"", ControllerHost = ""localhost"", ControllerPort = 40001 });

        public Task<Room> QuickLaunch(string serverType, CancellationToken ct, string host = null, int? port = null)
        {
            lock (Calls) Calls.Add(serverType + ""|"" + host + ""|"" + port + ""|"" + Thread.CurrentThread.ManagedThreadId);
            return Behaviour(serverType, ct, host, port);
        }
    }
}";

        public const string EngineSource = @"
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Gameloft.OnlinePlatform;

namespace Gameloft.Engine.LocalAnubis
{
    public class BaseServer
    {
        public bool Started { get; set; }
        public int Port { get; set; }
    }
    public class Controller : BaseServer { }
    public class Lobby : BaseServer { public Controller Controller { get; set; } }
    public class Finder : BaseServer
    {
        public string PublicHost { get { return ""localhost""; } }
        public int PublicPort { get; set; }
    }
}

namespace Gameloft.Engine
{
    using Gameloft.Engine.LocalAnubis;

    public class AuthenticationComponent { public Session Session { get; set; } }

    public class ClientComponent
    {
        public enum ServerLocation { Auto, None, Online, Remote, Embedded, SSH, Tunneling, P2P }
        public AuthenticationComponent Authentication { get; set; }
    }

    public class ClientSettings
    {
        public ClientComponent.ServerLocation ServerLocation { get; set; }
        public string ServerType { get; set; }
    }

    public class ClientLink
    {
        private Room room;
        public int StockCalls;
        public int RoomThread;
        public ClientSettings Settings { get; set; } = new ClientSettings();
        public Room Room { get { return room; } set { room = value; RoomThread = Thread.CurrentThread.ManagedThreadId; } }
        public Task QuickLaunch(CancellationToken ct) { StockCalls++; return Task.FromResult(0); }
    }

    /// <summary>A Game-side subclass, as the client's link may be.</summary>
    public class DerivedLink : ClientLink { }

    public class LocalAnubisManager
    {
        public static Lobby Lobby { get; set; }
        public static Finder Finder { get; set; }
        public static int StartCalls;
        public static int StartThread;
        public static bool Started { get { return Lobby != null && Lobby.Started && Finder != null && Finder.Started; } }

        /// <summary>Default: completes later on a pool thread with Lobby, Controller and Finder running.</summary>
        public static Func<CancellationToken, Task> Behaviour = ct => Task.Delay(30).ContinueWith(t => StartNow());

        public static void StartNow()
        {
            Lobby = new Lobby { Started = true, Port = 40000, Controller = new Controller { Started = true, Port = 40001 } };
            Finder = new Finder { Started = true, PublicPort = 51750 };
        }

        public static Task Start(CancellationToken ct)
        {
            StartCalls++;
            StartThread = Thread.CurrentThread.ManagedThreadId;
            return Behaviour(ct);
        }
    }

    public class MatchmakingComponent
    {
        protected class AutomatchSettings
        {
            public string Host { get; set; }
            public int? Port { get; set; }
        }

        public static object NewSettings(string host, int? port) { return new AutomatchSettings { Host = host, Port = port }; }
    }

    /// <summary>Settings whose Host setter throws.</summary>
    public class BrokenSettings
    {
        public string Host { get { return null; } set { throw new InvalidOperationException(""setter""); } }
        public int? Port { get; set; }
    }
}";

        private readonly FixtureCompiler.Sandbox sandbox;

        public PcLookAlikes()
        {
            byte[] online = FixtureCompiler.Compile("Gameloft.OnlinePlatform", OnlinePlatformSource);
            byte[] engine = FixtureCompiler.Compile("Gameloft.Engine", new[] { EngineSource }, new[] { online });
            sandbox = new FixtureCompiler.Sandbox().With("Gameloft.OnlinePlatform", online).With("Gameloft.Engine", engine);
            Online = sandbox.Get("Gameloft.OnlinePlatform");
            Engine = sandbox.Get("Gameloft.Engine");
        }

        public Assembly Engine { get; private set; }

        public Assembly Online { get; private set; }

        public Type EngineType(string name)
        {
            return Engine.GetType(name, true);
        }

        public Type OnlineType(string name)
        {
            return Online.GetType(name, true);
        }

        public object New(Type type)
        {
            return Activator.CreateInstance(type, true);
        }

        public static object Get(object target, string name)
        {
            return target.GetType().GetProperty(name).GetValue(target);
        }

        public static void Set(object target, string name, object value)
        {
            target.GetType().GetProperty(name).SetValue(target, value);
        }

        public object Static(string type, string name)
        {
            Type t = EngineType(type);
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            if (p != null) return p.GetValue(null);
            return t.GetField(name, BindingFlags.Public | BindingFlags.Static).GetValue(null);
        }

        public void SetStatic(string type, string name, object value)
        {
            Type t = EngineType(type);
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            if (p != null) p.SetValue(null, value);
            else t.GetField(name, BindingFlags.Public | BindingFlags.Static).SetValue(null, value);
        }

        /// <summary>A client component with a session and a link of <paramref name="location"/>.</summary>
        public object NewComponent(out object link, string location, string linkType = "Gameloft.Engine.ClientLink")
        {
            object session = New(OnlineType("Gameloft.OnlinePlatform.Session"));
            object auth = New(EngineType("Gameloft.Engine.AuthenticationComponent"));
            Set(auth, "Session", session);
            object component = New(EngineType("Gameloft.Engine.ClientComponent"));
            Set(component, "Authentication", auth);
            link = New(EngineType(linkType));
            object settings = Get(link, "Settings");
            Type locationType = EngineType("Gameloft.Engine.ClientComponent+ServerLocation");
            Set(settings, "ServerLocation", Enum.Parse(locationType, location));
            Set(settings, "ServerType", "ts");
            return component;
        }

        public void Dispose()
        {
            sandbox.Dispose();
        }
    }

    /// <summary>A SynchronizationContext with one thread of its own (the game thread of the tests).</summary>
    internal sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        private readonly Thread thread;

        public SingleThreadContext()
        {
            thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (Action action in queue.GetConsumingEnumerable())
                {
                    try
                    {
                        action();
                    }
                    catch (Exception)
                    {
                    }
                }
            });
            thread.IsBackground = true;
            thread.Name = "test-game-thread";
            thread.Start();
        }

        public int ThreadId { get { return thread.ManagedThreadId; } }

        public override void Post(SendOrPostCallback d, object state)
        {
            queue.Add(() => d(state));
        }

        public override void Send(SendOrPostCallback d, object state)
        {
            Invoke(() =>
            {
                d(state);
                return 0;
            });
        }

        /// <summary>Runs <paramref name="func"/> on the context's thread and waits for its result.</summary>
        public T Invoke<T>(Func<T> func)
        {
            T result = default(T);
            Exception error = null;
            using (var done = new ManualResetEventSlim(false))
            {
                queue.Add(() =>
                {
                    try
                    {
                        result = func();
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        done.Set();
                    }
                });
                if (!done.Wait(10000)) throw new TimeoutException("the context thread did not run the call");
            }
            if (error != null) throw new InvalidOperationException("the call threw", error);
            return result;
        }

        public void Dispose()
        {
            queue.CompleteAdding();
        }
    }
}
