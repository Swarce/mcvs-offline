using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Server.Core;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// PcHooks executed against look-alikes of the PC engine (docs/design/pc-port.md §8.3, §12.2): the quick launch through
    /// LocalAnubis on a single-thread "game" SynchronizationContext, every failure as a faulted task with its own exception
    /// type, a log line in every catch (each injected failure gives exactly one WARN), and DefaultAutomatchHost.
    /// </summary>
    [Collection("GameHooks")]
    public class PcHooksTests : IDisposable
    {
        private readonly MemoryLogSink sink = new MemoryLogSink(LogLevel.Debug, 500);
        private readonly PcLookAlikes game = new PcLookAlikes();
        private readonly SingleThreadContext context = new SingleThreadContext();

        public PcHooksTests()
        {
            PcHooks.ResetForTests();
            var logger = new Logger();
            logger.AddSink(sink);
            PcHooks.Configure(logger.For("pc"), null);
        }

        public void Dispose()
        {
            PcHooks.ResetForTests();
            context.Dispose();
            game.Dispose();
        }

        private int mark;

        /// <summary>From here on, Warnings() and Lines() see only new entries.</summary>
        private void Mark()
        {
            mark = sink.Snapshot().Count;
        }

        private List<LogEntry> Warnings()
        {
            return sink.Snapshot().Skip(mark).Where(e => e.Level == LogLevel.Warn).ToList();
        }

        private List<string> Lines()
        {
            return sink.Snapshot().Skip(mark).Select(e => e.Format()).ToList();
        }

        /// <summary>PcHooks.QuickLaunch called on the context thread, as the patched ClientComponent does on the game thread.</summary>
        private Task Launch(object component, object link, CancellationToken ct = default(CancellationToken))
        {
            Task task = context.Invoke(() => PcHooks.QuickLaunch(component, link, ct));
            Assert.NotNull(task);
            return task;
        }

        /// <summary>True when the task ran to completion within 10 s.</summary>
        private static bool Completes(Task task)
        {
            return ((IAsyncResult)task).AsyncWaitHandle.WaitOne(10000) && task.Status == TaskStatus.RanToCompletion;
        }

        private static Exception WaitFault(Task task)
        {
            Assert.True(((IAsyncResult)task).AsyncWaitHandle.WaitOne(10000), "the task did not end");
            Assert.True(task.IsFaulted, "expected a faulted task, was " + task.Status);
            return task.Exception.InnerExceptions.Single();
        }

        private List<string> MatchmakerCalls()
        {
            var calls = (List<string>)game.OnlineType("Gameloft.OnlinePlatform.MatchmakerService").GetField("Calls").GetValue(null);
            lock (calls) return new List<string>(calls);
        }

        [Fact]
        public void EmbeddedStartsLocalAnubisOnceAndQuickLaunchesThroughTheLocalFinderOnTheGameThread()
        {
            object link;
            object component = game.NewComponent(out link, "Embedded");
            Task task = Launch(component, link);
            Assert.True(Completes(task));
            Assert.Equal(1, (int)game.Static("Gameloft.Engine.LocalAnubisManager", "StartCalls"));
            Assert.Equal(context.ThreadId, (int)game.Static("Gameloft.Engine.LocalAnubisManager", "StartThread"));
            List<string> calls = MatchmakerCalls();
            Assert.Single(calls);
            Assert.Equal("ts|localhost|51750|" + context.ThreadId, calls[0]);   // after the start finished on a pool thread: back on the game thread
            object room = PcLookAlikes.Get(link, "Room");
            Assert.NotNull(room);
            Assert.Equal("room-1", PcLookAlikes.Get(room, "Id"));
            Assert.Equal(context.ThreadId, (int)link.GetType().GetField("RoomThread").GetValue(link));
            Assert.Equal(0, (int)link.GetType().GetField("StockCalls").GetValue(link));
            Assert.Empty(Warnings());
            Assert.Contains(Lines(), l => l.Contains("[pc] quicklaunch: LocalAnubis started (Finder http://localhost:51750/, lobby port 40000, controller port 40001"));
            Assert.Contains(Lines(), l => l.Contains("[pc] quicklaunch: room room-1 (controller localhost:40001, thread "));

            // Already started: no second Start.
            object link2;
            object component2 = game.NewComponent(out link2, "Embedded", "Gameloft.Engine.DerivedLink");
            Assert.True(Completes(Launch(component2, link2)));
            Assert.Equal(1, (int)game.Static("Gameloft.Engine.LocalAnubisManager", "StartCalls"));
            Assert.Equal(2, MatchmakerCalls().Count);
            Assert.NotNull(PcLookAlikes.Get(link2, "Room"));
        }

        [Fact]
        public void NotEmbeddedTakesTheGamesOwnPathOnly()
        {
            object link;
            object component = game.NewComponent(out link, "Online");
            Task task = Launch(component, link);
            Assert.True(Completes(task));
            Assert.Equal(1, (int)link.GetType().GetField("StockCalls").GetValue(link));
            Assert.Equal(0, (int)game.Static("Gameloft.Engine.LocalAnubisManager", "StartCalls"));
            Assert.Empty(MatchmakerCalls());
            Assert.Contains(Lines(), l => l.Contains("[pc] quicklaunch: ServerLocation Online: the game's own path"));
            Assert.True(Completes(Launch(component, link)));
            Assert.Single(Lines(), l => l.Contains("ServerLocation Online"));   // logged once
        }

        [Fact]
        public void AFailedStartFaultsWithTheSameExceptionAndNamesHttpSys()
        {
            var refused = new HttpListenerException(5, "Access is denied");
            game.SetStatic("Gameloft.Engine.LocalAnubisManager", "Behaviour",
                           (Func<CancellationToken, Task>)(ct => Task.Delay(10).ContinueWith(t => { throw refused; })));
            object link;
            object component = game.NewComponent(out link, "Embedded");
            Exception fault = WaitFault(Launch(component, link));
            Assert.Same(refused, fault);
            List<LogEntry> warnings = Warnings();
            Assert.Single(warnings);
            Assert.Contains("quicklaunch: LocalAnubis did not start: HttpListenerException", warnings[0].Message);
            Assert.Contains("http.sys refused http://localhost:51750/", warnings[0].Message);
            Assert.Empty(MatchmakerCalls());
        }

        [Fact]
        public void AStartThatThrowsAtOnceFaultsWithoutThrowing()
        {
            var boom = new InvalidOperationException("no super credential");
            game.SetStatic("Gameloft.Engine.LocalAnubisManager", "Behaviour", (Func<CancellationToken, Task>)(ct => { throw boom; }));
            object link;
            object component = game.NewComponent(out link, "Embedded");
            Exception fault = WaitFault(Launch(component, link));
            Assert.Same(boom, fault);   // unwrapped from the reflection call
            Assert.Single(Warnings());
            Assert.Contains("quicklaunch: cannot start: InvalidOperationException", Warnings()[0].Message);
        }

        [Fact]
        public void AFaultedQuickLaunchFaultsTheTask()
        {
            var noRoom = new TimeoutException("no room");
            game.OnlineType("Gameloft.OnlinePlatform.MatchmakerService").GetField("Behaviour").SetValue(null,
                (Func<string, CancellationToken, string, int?, Task>)null);
            SetMatchmaker((type, ct, host, port) => Task.Delay(10).ContinueWith<object>(t => { throw noRoom; }));
            object link;
            object component = game.NewComponent(out link, "Embedded");
            Exception fault = WaitFault(Launch(component, link));
            Assert.Same(noRoom, fault);
            Assert.Single(Warnings());
            Assert.Contains("quicklaunch: the local Finder answered no room: TimeoutException", Warnings()[0].Message);
            Assert.Null(PcLookAlikes.Get(link, "Room"));
        }

        [Fact]
        public void ACancelledQuickLaunchCancelsTheTask()
        {
            using (var cts = new CancellationTokenSource())
            {
                SetMatchmaker((type, ct, host, port) =>
                {
                    cts.Cancel();
                    var source = new TaskCompletionSource<object>();
                    source.SetCanceled();
                    return source.Task;
                });
                object link;
                object component = game.NewComponent(out link, "Embedded");
                Task task = Launch(component, link, cts.Token);
                Assert.True(((IAsyncResult)task).AsyncWaitHandle.WaitOne(10000));
                Assert.True(task.IsCanceled);
                Assert.Empty(Warnings());
            }
        }

        [Fact]
        public void AMissingMemberFaultsWithOneWarning()
        {
            object link;
            object component = game.NewComponent(out link, "Embedded");
            PcLookAlikes.Set(PcLookAlikes.Get(component, "Authentication"), "Session", null);   // no session on the client
            Exception fault = WaitFault(Launch(component, link));
            Assert.IsType<InvalidOperationException>(fault);
            Assert.Single(Warnings());
            Assert.Contains("quicklaunch: the local quick launch could not be sent", Warnings()[0].Message);

            Mark();
            Exception notALink = WaitFault(Launch(component, new object()));
            Assert.IsType<MissingMemberException>(notALink);
            Assert.Single(Warnings());
            Assert.Contains("quicklaunch: cannot start", Warnings()[0].Message);

            Mark();
            Exception nullLink = WaitFault(Launch(component, null));
            Assert.IsType<ArgumentNullException>(nullLink);
            Assert.Single(Warnings());
        }

        [Fact]
        public void WithoutASynchronizationContextItWarnsAndStillCompletes()
        {
            object link;
            object component = game.NewComponent(out link, "Embedded");
            Task task = null;
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(null);
                task = PcHooks.QuickLaunch(component, link, CancellationToken.None);
            });
            thread.Start();
            Assert.True(thread.Join(10000));
            Assert.True(Completes(task));
            Assert.NotNull(PcLookAlikes.Get(link, "Room"));
            Assert.Single(Warnings());
            Assert.Contains("no synchronization context", Warnings()[0].Message);
        }

        [Fact]
        public void TheLogIsRateLimitedPerMessage()
        {
            for (int i = 0; i < 60; i++) Assert.True(WaitFault(Launch(null, null)) is ArgumentNullException);
            Assert.Equal(6, Warnings().Count);   // the first 5, then the 50th
        }

        // ------------------------------------------------------------------ P3

        [Fact]
        public void DefaultAutomatchHostFillsEmptySettingsOnlyAfterOurStart()
        {
            MethodInfo make = game.EngineType("Gameloft.Engine.MatchmakingComponent").GetMethod("NewSettings");
            object empty = make.Invoke(null, new object[] { null, null });
            PcHooks.DefaultAutomatchHost(empty);   // not started: untouched
            Assert.Null(PcLookAlikes.Get(empty, "Host"));
            Assert.Null(PcLookAlikes.Get(empty, "Port"));

            game.EngineType("Gameloft.Engine.LocalAnubisManager").GetMethod("StartNow").Invoke(null, null);
            PcHooks.DefaultAutomatchHost(empty);
            Assert.Equal("localhost", PcLookAlikes.Get(empty, "Host"));
            Assert.Equal(51750, PcLookAlikes.Get(empty, "Port"));
            Assert.Contains(Lines(), l => l.Contains("[pc] quicklaunch: automatch through the local Finder localhost:51750"));

            object seeded = make.Invoke(null, new object[] { "10.0.0.2", 1234 });
            PcHooks.DefaultAutomatchHost(seeded);
            Assert.Equal("10.0.0.2", PcLookAlikes.Get(seeded, "Host"));
            Assert.Equal(1234, PcLookAlikes.Get(seeded, "Port"));
            object portOnly = make.Invoke(null, new object[] { null, 999 });
            PcHooks.DefaultAutomatchHost(portOnly);
            Assert.Null(PcLookAlikes.Get(portOnly, "Host"));
            PcHooks.DefaultAutomatchHost(null);
            Assert.Empty(Warnings());
        }

        [Fact]
        public void DefaultAutomatchHostWithAThrowingSetterWarnsAndReturns()
        {
            game.EngineType("Gameloft.Engine.LocalAnubisManager").GetMethod("StartNow").Invoke(null, null);
            object broken = game.New(game.EngineType("Gameloft.Engine.BrokenSettings"));
            PcHooks.DefaultAutomatchHost(broken);
            Assert.Single(Warnings());
            Assert.Contains("quicklaunch: the automatch host could not be set: InvalidOperationException", Warnings()[0].Message);
            PcHooks.DefaultAutomatchHost(new object());   // no LocalAnubisManager next to it
            Assert.Equal(2, Warnings().Count);
        }

        // ------------------------------------------------------------------ M1, M4

        [Fact]
        public void ServerWorldAndComponentFailuresAreLoggedAndCapped()
        {
            for (int i = 0; i < 12; i++) PcHooks.ServerWorldFailed(new InvalidOperationException("world " + i));
            List<LogEntry> warnings = Warnings();
            Assert.Equal(10, warnings.Count);
            Assert.All(warnings, w => Assert.StartsWith("server world: ServerManager._AddClientAsync failed: InvalidOperationException", w.Message));
            Assert.NotNull(warnings[0].Exception);

            Mark();
            PcHooks.ComponentCreateFailed(new NullReferenceException(), typeof(string));
            PcHooks.ComponentCreateFailed(new NullReferenceException(), typeof(string));   // the same type: once
            PcHooks.ComponentCreateFailed(null, null);
            warnings = Warnings();
            Assert.Equal(2, warnings.Count);
            Assert.Equal("component System.String was not created: NullReferenceException: Object reference not set to an instance of an object.", warnings[0].Message);
            Assert.Contains("component ? was not created", warnings[1].Message);
            Assert.Contains("server World failures 12, components not created 3", PcHooks.Describe());
        }

        [Fact]
        public void ServerWorldAndComponentLinesHaveTheHeadlessWorldShape()
        {
            // Like the H3 hook's "[boot] headless world: ..." (GameHooks.HeadlessComponentException): no tag inside the message.
            var logger = new Logger();
            var lines = new MemoryLogSink(LogLevel.Debug, 50);
            logger.AddSink(lines);
            PcHooks.Configure(logger.For("pc"), new BootTrace(logger.For("boot")));
            PcHooks.ServerWorldFailed(new InvalidOperationException("world"));
            PcHooks.ComponentCreateFailed(new NullReferenceException(), typeof(int));
            List<LogEntry> entries = lines.Snapshot().ToList();
            Assert.Contains(entries, e => e.Category == "boot" && Regex.IsMatch(e.Message, "^\\+[0-9.]+ s server world: ServerManager._AddClientAsync failed: InvalidOperationException"));
            Assert.Contains(entries, e => e.Category == "boot" && Regex.IsMatch(e.Message, "^\\+[0-9.]+ s component System.Int32 was not created: NullReferenceException"));
            Assert.Contains(entries, e => e.Category == "pc" && e.Message.StartsWith("server world: ", StringComparison.Ordinal));
            Assert.Contains(entries, e => e.Category == "pc" && e.Message.StartsWith("component System.Int32 was not created: ", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, e => e.Format().Contains("[McvsOffline]"));
        }

        private void SetMatchmaker(Func<string, CancellationToken, string, int?, Task<object>> behaviour)
        {
            // The look-alike's delegate returns Task<Room>; wrap the test's Task<object> into one.
            Type roomType = game.OnlineType("Gameloft.OnlinePlatform.Room");
            MethodInfo wrap = typeof(PcHooksTests).GetMethod("Wrap", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(roomType);
            Type delegateType = typeof(Func<,,,,>).MakeGenericType(typeof(string), typeof(CancellationToken), typeof(string), typeof(int?),
                                                                   typeof(Task<>).MakeGenericType(roomType));
            Delegate converted = (Delegate)wrap.Invoke(null, new object[] { behaviour });
            Assert.Equal(delegateType, converted.GetType());
            game.OnlineType("Gameloft.OnlinePlatform.MatchmakerService").GetField("Behaviour").SetValue(null, converted);
        }

        private static Func<string, CancellationToken, string, int?, Task<T>> Wrap<T>(Func<string, CancellationToken, string, int?, Task<object>> inner)
        {
            return (type, ct, host, port) =>
            {
                var source = new TaskCompletionSource<T>();
                inner(type, ct, host, port).ContinueWith(t =>
                {
                    if (t.IsFaulted) source.SetException(t.Exception.InnerExceptions);
                    else if (t.IsCanceled) source.SetCanceled();
                    else source.SetResult((T)t.Result);
                }, TaskScheduler.Default);
                return source.Task;
            };
        }
    }
}
