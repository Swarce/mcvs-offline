// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using McvsOffline.Server.Core;

namespace McvsOffline
{
    /// <summary>
    /// Entry points the patched game code calls (patcher/Patches: NativeOnlinePatch, HeadlessEnginePatch, PhysicsPatch, StorePatch, InboxPatch,
    /// BotMovePatch). Their
    /// signatures are part of the patch contract (the patcher looks them up by name and parameter types). They run on the game
    /// thread or a loading-pool thread: cheap, never throw (the patched IL also guards every call), and the only one that can
    /// wait is <see cref="EnterPhysicsStep"/>, for at most <c>embedded.physics.stepGateTimeoutMs</c>.
    /// </summary>
    public static class GameHooks
    {
        private static volatile bool allowNativeOnline;
        private static volatile Log log;
        private static volatile BootTrace boot;
        private static int nativeOnlineCalls;
        private static int headlessReports;
        private static int storeReports;
        private static readonly System.Collections.Generic.HashSet<string> storeReported = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        private static int inboxReports;
        private static readonly System.Collections.Generic.HashSet<string> inboxReported = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        // Physics (device test 2). The defaults are the fix; settings.json can revert each part for an A/B run.
        private static volatile bool serverPhysicsMultithreaded;
        private static volatile bool clientPhysicsMultithreaded = true;
        private static volatile bool physicsStepGate = true;
        private static volatile int physicsStepGateTimeoutMs = DefaultStepGateTimeoutMs;
        private static readonly object physicsGate = new object();
        private static int physicsGateOwner;   // managed thread id of the World stepping now, 0 = free (under physicsGate)
        private static int physicsWorlds;
        private static int serverWorldsSingleThreaded;
        private static int physicsSteps;
        private static int physicsContended;
        private static int physicsMaxWaitMs;
        private static int physicsTimeouts;

        /// <summary>How long a physics step waits for another World's step before it goes ahead without the gate.</summary>
        public const int DefaultStepGateTimeoutMs = 2000;

        /// <summary>Set by the embedded host once settings.json is read (before the game creates any World).</summary>
        public static void Configure(bool nativeOnlineSdk, Log hostLog, BootTrace bootTrace)
        {
            allowNativeOnline = nativeOnlineSdk;
            log = hostLog;
            boot = bootTrace;
        }

        /// <summary>
        /// settings.json <c>embedded.physics</c> (device test 2): <paramref name="serverMultithreaded"/> lets TS/GS Worlds keep the
        /// game's multithreaded Havok (default false, as on Gameloft's dedicated servers), <paramref name="clientMultithreaded"/>
        /// (default true) the client World, <paramref name="stepGate"/> (default true) serializes every World's physics step.
        /// </summary>
        public static void ConfigurePhysics(bool serverMultithreaded, bool clientMultithreaded, bool stepGate, int stepGateTimeoutMs)
        {
            serverPhysicsMultithreaded = serverMultithreaded;
            clientPhysicsMultithreaded = clientMultithreaded;
            physicsStepGate = stepGate;
            physicsStepGateTimeoutMs = stepGateTimeoutMs < 1 ? 1 : stepGateTimeoutMs;
        }

        /// <summary>Times the game asked to start the native online SDK (tests, status line).</summary>
        public static int NativeOnlineCalls { get { return nativeOnlineCalls; } }

        /// <summary>Headless-World component exceptions reported so far.</summary>
        public static int HeadlessReports { get { return headlessReports; } }

        /// <summary>Physics steps that went through the gate, contended entries, and entries that timed out (status line, tests).</summary>
        public static int PhysicsSteps { get { return physicsSteps; } }

        public static int PhysicsContended { get { return physicsContended; } }

        public static int PhysicsTimeouts { get { return physicsTimeouts; } }

        /// <summary>
        /// Called by <c>OnlinePlatformComponent.Init_private</c> (the managed side of the native "GaiaInit"), once per World
        /// that knows its datacenter. True lets the native online SDK start (settings.json <c>embedded.nativeOnlineSdk</c>).
        /// </summary>
        public static bool AllowNativeOnlineInit(string datacenter)
        {
            try
            {
                bool allow = allowNativeOnline;
                int n = Interlocked.Increment(ref nativeOnlineCalls);
                if (n <= 5)
                {
                    Mark("native online SDK init requested (OnlinePlatformComponent.Init_private, datacenter '" + datacenter + "', call " +
                         n.ToString(CultureInfo.InvariantCulture) + "): " +
                         (allow ? "started, settings embedded.nativeOnlineSdk is true" : "skipped, settings embedded.nativeOnlineSdk is false"));
                }
                return allow;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Called by the headless-World safety net (<c>McvsHeadlessSafeAction</c> in Gameloft.Engine) when a component event
        /// of a TS/GS World threw; the rest of that event was skipped. Each line names a component that needs a precise fix.
        /// </summary>
        public static void HeadlessComponentException(string component, string eventName, Exception exception)
        {
            try
            {
                int n = Interlocked.Increment(ref headlessReports);
                if (n > 100) return;
                string text = "headless world: " + component + "." + eventName + " threw, the rest of it is skipped (report " +
                              n.ToString(CultureInfo.InvariantCulture) + ")";
                BootTrace b = boot;
                if (b != null) b.Warn(text);
                Log l = log;
                if (l != null) l.Warn(text, exception);
                else SelfLog.Write(SelfLog.Warn, "[headless] " + text + ": " + exception);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Called by the shop's safety net (patch 'store', <c>Game.McvsStoreSafety.Report</c>) when the availability check of one
        /// unified-store item threw; that item is left out of the shop. One line per item id (the shop rebuilds its lists on every
        /// visit), on stdout (logcat mono-stdout) and in bootstrap.log.
        /// </summary>
        public static void StoreItemException(object item, Exception exception)
        {
            try
            {
                string id = ItemId(item);
                lock (storeReported)
                {
                    if (storeReported.Count >= 100 || !storeReported.Add(id)) return;
                }
                Interlocked.Increment(ref storeReports);
                string text = "store item " + id + " threw " + (exception == null ? "?" : exception.ToString());
                try
                {
                    Console.WriteLine("[McvsOffline] " + text);
                }
                catch (Exception)
                {
                }
                Log l = log;
                if (l != null) l.Warn("store item " + id + " is left out of the shop: its availability check threw", exception);
                else SelfLog.Write(SelfLog.Warn, "[store] " + text);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Items the store safety net reported (distinct ids).</summary>
        public static int StoreReports { get { return storeReports; } }

        /// <summary>
        /// Called by the inbox patch (<c>Game.McvsInbox.Report</c>) when <c>RewardItem.GetObject</c> threw for one reward of an inbox
        /// message, or while the claim popup's list was built; that reward is left out of what the inbox shows (a claim still
        /// claims it). One line per reward id (the inbox rebuilds its list every 10 s while open), on stdout (logcat mono-stdout)
        /// and in bootstrap.log.
        /// </summary>
        public static void InboxRewardException(object reward, Exception exception)
        {
            try
            {
                string id = ItemId(reward);
                lock (inboxReported)
                {
                    if (inboxReported.Count >= 100 || !inboxReported.Add(id)) return;
                }
                Interlocked.Increment(ref inboxReports);
                string text = "inbox reward " + id + " threw " + (exception == null ? "?" : exception.ToString());
                try
                {
                    Console.WriteLine("[McvsOffline] " + text);
                }
                catch (Exception)
                {
                }
                Log l = log;
                if (l != null) l.Warn("inbox reward " + id + " is left out of the inbox list: building its entry threw", exception);
                else SelfLog.Write(SelfLog.Warn, "[inbox] " + text);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Inbox rewards the inbox patch reported (distinct ids).</summary>
        public static int InboxReports { get { return inboxReports; } }

        private static string ItemId(object item)
        {
            if (item == null) return "(null)";
            try
            {
                PropertyInfo p = item.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object v = p == null ? null : p.GetValue(item, null);
                if (v != null) return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
            }
            return item.GetType().FullName;
        }

        /// <summary>
        /// Called by the <c>PhysicsComponent</c> constructor (patch 'physics', <c>McvsPhysics.UseMultithreading</c>) for every
        /// World's Havok world, with the game's own choice (<c>PhysicsComponent.PhysicsMultithreaded</c>, from
        /// GameOptions_android.json: true) and whether the World is a headless TS/GS. Returns what the native constructor gets.
        /// <para>
        /// Multithreaded Havok worlds all share ONE process-wide hkCpuThreadPool and hkJobQueue, which support one stepping thread
        /// at a time. Gameloft ran each server World alone in its own Linux process without GameOptions (single-threaded Havok);
        /// in-process, the client World stepped on a loading-pool thread while the TS World stepped on the game thread, through
        /// the same pool, and the game thread never came back (device test 2). Server Worlds therefore get single-threaded Havok.
        /// </para>
        /// </summary>
        public static bool PhysicsMultithreading(bool headless, bool requested)
        {
            bool use = requested;
            try
            {
                use = requested && (headless ? serverPhysicsMultithreaded : clientPhysicsMultithreaded);
                if (headless && requested && !use) Interlocked.Increment(ref serverWorldsSingleThreaded);
                int n = Interlocked.Increment(ref physicsWorlds);
                if (n <= 8)
                {
                    string text = "physics: " + (headless ? "server (headless) World" : "client World") + " #" + n.ToString(CultureInfo.InvariantCulture) +
                                  ": Havok " + (use ? "multithreaded" : "single-threaded") + " (GameOptions multithreadedPhysics " + Bool(requested) +
                                  (headless ? ", settings embedded.physics.serverMultithreaded " + Bool(serverPhysicsMultithreaded)
                                            : ", settings embedded.physics.clientMultithreaded " + Bool(clientPhysicsMultithreaded)) +
                                  "; step gate " + (physicsStepGate ? "on" : "off") + ")";
                    if (n == 1) text += "; " + DescribeEngineThreading();
                    Mark(text);
                }
            }
            catch (Exception)
            {
            }
            return use;
        }

        /// <summary>
        /// Called by <c>PhysicsComponent.UpdateSimulationSteps</c> (patch 'physics') before a World's 1-4 Havok steps, on the
        /// thread that steps (the game thread, or a loading-pool thread for the client's deferred physics). True: the caller
        /// holds the process-wide step gate and must call <see cref="ExitPhysicsStep"/>. False: gate off, or another World's step
        /// held it for longer than the timeout (logged), and the caller steps without it, as the original game did.
        /// </summary>
        public static bool EnterPhysicsStep()
        {
            try
            {
                if (!physicsStepGate) return false;
                Interlocked.Increment(ref physicsSteps);
                int me = Thread.CurrentThread.ManagedThreadId;
                int waited = -1;
                bool taken = false;
                // The game's trimmed mscorlib has no timed Monitor.TryEnter: the gate is an owner field under a short lock, and a
                // waiter sleeps in Monitor.Wait(lock, remaining), which releases the lock.
                lock (physicsGate)
                {
                    if (physicsGateOwner == 0)
                    {
                        physicsGateOwner = me;
                        return true;
                    }
                    if (physicsGateOwner == me) return false;   // re-entry (not expected): the outer call releases it
                    int started = Environment.TickCount;
                    int timeout = physicsStepGateTimeoutMs;
                    while (physicsGateOwner != 0)
                    {
                        int left = timeout - (Environment.TickCount - started);
                        if (left <= 0) break;
                        Monitor.Wait(physicsGate, left);
                    }
                    if (physicsGateOwner == 0)
                    {
                        physicsGateOwner = me;
                        taken = true;
                    }
                    waited = Environment.TickCount - started;
                }
                Interlocked.Increment(ref physicsContended);
                if (waited > physicsMaxWaitMs) physicsMaxWaitMs = waited;
                if (!taken)
                {
                    int n = Interlocked.Increment(ref physicsTimeouts);
                    if (n <= 5)
                    {
                        Warn("physics step gate: another World's physics step held it for " + waited.ToString(CultureInfo.InvariantCulture) +
                             " ms; stepping without the gate (timeout " + n.ToString(CultureInfo.InvariantCulture) + ", thread " +
                             me.ToString(CultureInfo.InvariantCulture) + ")");
                    }
                }
                return taken;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Releases the gate taken by <see cref="EnterPhysicsStep"/> (same thread; only when it returned true).</summary>
        public static void ExitPhysicsStep()
        {
            try
            {
                int me = Thread.CurrentThread.ManagedThreadId;
                lock (physicsGate)
                {
                    if (physicsGateOwner != me) return;
                    physicsGateOwner = 0;
                    Monitor.PulseAll(physicsGate);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>One status-line part: what the physics hooks decided and did.</summary>
        public static string DescribePhysics()
        {
            int worlds = physicsWorlds;
            if (worlds == 0) return "physics: no World built yet";
            return "physics: " + worlds.ToString(CultureInfo.InvariantCulture) + " Havok worlds built (" +
                   serverWorldsSingleThreaded.ToString(CultureInfo.InvariantCulture) + " server Worlds made single-threaded), step gate " +
                   (physicsStepGate ? "on: " + physicsSteps.ToString(CultureInfo.InvariantCulture) + " entries, " +
                                      physicsContended.ToString(CultureInfo.InvariantCulture) + " waited (max " +
                                      physicsMaxWaitMs.ToString(CultureInfo.InvariantCulture) + " ms), " +
                                      physicsTimeouts.ToString(CultureInfo.InvariantCulture) + " timeouts"
                                    : "off");
        }

        // ------------------------------------------------------------------ bots (patch 'botmove', build 27)

        /// <summary><c>BT_Action_MoveTo.moveToVector</c> stops a bot whose speed is not above this.</summary>
        public const float BotStartThreshold = 0.05f;

        /// <summary>The first step a resting bot gets when its own is at or under the threshold: a 17 ms frame's 3*dt.</summary>
        public const float BotFirstStep = 0.051f;

        /// <summary>A "bots:" line every 30 s while MoveTo ramp calls arrive.</summary>
        internal const int BotLineIntervalMs = 30000;

        /// <summary>A ramp call after this long without one starts a new match (the counters restart).</summary>
        internal const int BotMatchGapMs = 60000;

        /// <summary>The p50 is taken over the last this many AI-tick dts of the match (a ring; min and max cover the whole match).</summary>
        internal const int BotDtSamples = 4096;

        private static volatile bool botMoveFromRest = true;
        private static readonly object botGate = new object();
        private static readonly float[] botDt = new float[BotDtSamples];
        private static int botCalls, botFromRest, botBlocked, botHelped, botShortFrames, botMatches, botHeadlessBase;
        private static float botMinDt, botMaxDt;
        private static int botLastCall, botLastLine, botFirstBlockLogged;
        private static int botInputResets;
        private static volatile string botWorldLine;

        /// <summary>Milliseconds for the match and line timers (tests replace it).</summary>
        internal static Func<int> BotClock = () => Environment.TickCount;

        /// <summary>settings.json <c>embedded.bots.moveFromRest</c> (default true): a resting bot always takes its first step.</summary>
        public static void ConfigureBots(bool moveFromRest)
        {
            botMoveFromRest = moveFromRest;
        }

        /// <summary>
        /// Called by <c>BT_Action_MoveTo.adaptSpeed</c> (patch 'botmove', <c>Gameloft.Sequences.McvsBotMove.Ramp</c>) on the game
        /// thread, for a bot whose speed is ramping (an AI tick of a match server's World): <paramref name="ramped"/> is the game's
        /// <c>Sqrt(prevSq) + dt * acceleration</c>, <paramref name="prevSq"/> the bot's input Forward² + Strafe² (0 = at rest),
        /// <paramref name="dt"/> the frame's DeltaTime. Returns the speed the game goes on with: <paramref name="ramped"/> unchanged,
        /// except a step at or under 0.05 (which <c>moveToVector</c> would turn into a stop) is raised to 0.051 while
        /// <c>embedded.bots.moveFromRest</c> is on. 3 * dt is above 0.05 for every frame of at least 1/60 s, so the game's own
        /// value comes back bit for bit there; the PC's 60 FPS limit ends its frames about 0.1 ms early (dt 16.57 ms, 3*dt =
        /// 0.0497), which left every resting bot where it stood. Counts what it sees for the "bots:" lines. Never throws.
        /// </summary>
        public static float BotMoveRamp(float ramped, float prevSq, float dt)
        {
            float result = ramped;
            try
            {
                bool blocked = ramped <= BotStartThreshold;
                bool help = blocked && botMoveFromRest;
                if (help) result = BotFirstStep;
                RecordBotRamp(ramped, prevSq, dt, blocked, help);
            }
            catch (Exception)
            {
            }
            return result;
        }

        private static void RecordBotRamp(float ramped, float prevSq, float dt, bool blocked, bool help)
        {
            int now = BotClock();
            string[] finished = null;
            bool newMatch = false, line = false;
            lock (botGate)
            {
                if (botCalls > 0 && now - botLastCall >= BotMatchGapMs)
                {
                    finished = BotLines();
                    ClearBotStats();
                }
                if (botCalls == 0)
                {
                    newMatch = true;
                    botMatches++;
                    botHeadlessBase = headlessReports;
                    botLastLine = now;
                    botMinDt = dt;
                    botMaxDt = dt;
                }
                botLastCall = now;
                botCalls++;
                if (prevSq == 0f) botFromRest++;
                if (blocked) botBlocked++;
                if (help) botHelped++;
                if (dt < 1f / 60f) botShortFrames++;
                if (dt < botMinDt) botMinDt = dt;
                if (dt > botMaxDt) botMaxDt = dt;
                botDt[(botCalls - 1) % BotDtSamples] = dt;
                if (now - botLastLine >= BotLineIntervalMs)
                {
                    botLastLine = now;
                    line = true;
                }
            }
            if (finished != null)
            {
                foreach (string text in finished) BotInfo(text.Replace("bots: ", "bots: (match ended) "));
            }
            if (newMatch)
            {
                botWorldLine = DescribeBotWorlds();
                BotInfo("bots: match " + botMatches.ToString(CultureInfo.InvariantCulture) + ": the first MoveTo ramp call (settings embedded.bots.moveFromRest " +
                        Bool(botMoveFromRest) + "); " + botWorldLine.Substring("bots: ".Length));
            }
            if (blocked && Interlocked.Exchange(ref botFirstBlockLogged, 1) == 0)
            {
                Mark("bots: a bot's step from speed " + Number(prevSq <= 0f ? 0f : Math.Sqrt(prevSq), "0.000") + " was " + Number(ramped, "0.0000") +
                     " (frame " + Ms(dt) + " ms, under 1/60 s), not above the game's 0.05 start threshold: " +
                     (help ? "raised to " + Number(BotFirstStep, "0.000") + " (settings embedded.bots.moveFromRest true)"
                           : "left as it is, the bot stays where it is (settings embedded.bots.moveFromRest false)"));
            }
            if (line)
            {
                botWorldLine = DescribeBotWorlds();
                foreach (string text in BotLinesLocked()) BotInfo(text);
            }
        }

        /// <summary>Seconds as milliseconds with three decimals ("16.566"): 1/60 s is 16.667.</summary>
        private static string Ms(float seconds)
        {
            return Number(seconds * 1000.0, "0.000");
        }

        private static string Number(double value, string format)
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        private static void ClearBotStats()
        {
            botCalls = botFromRest = botBlocked = botHelped = botShortFrames = 0;
            for (int i = 0; i < botDt.Length; i++) botDt[i] = 0f;
        }

        private static string[] BotLinesLocked()
        {
            lock (botGate) return BotLines();
        }

        /// <summary>The three "bots:" lines of the current match (under <see cref="botGate"/>).</summary>
        private static string[] BotLines()
        {
            var ramp = new System.Text.StringBuilder("bots: MoveTo ramp ");
            ramp.Append(botCalls.ToString(CultureInfo.InvariantCulture)).Append(" calls, ").Append(botFromRest.ToString(CultureInfo.InvariantCulture))
                .Append(" from rest; ").Append(botBlocked.ToString(CultureInfo.InvariantCulture)).Append(" under the 0.05 start threshold (moveFromRest ")
                .Append(botMoveFromRest ? "on: " + botHelped.ToString(CultureInfo.InvariantCulture) + " helped" : "off: none raised").Append(')');
            if (botCalls > 0)
            {
                ramp.Append("; AI-tick dt ms min ").Append(Ms(botMinDt)).Append(" p50 ").Append(Ms(BotMedian()))
                    .Append(" max ").Append(Ms(botMaxDt)).Append(", ")
                    .Append(((botShortFrames * 100L + botCalls / 2) / botCalls).ToString(CultureInfo.InvariantCulture)).Append("% under 1/60 s");
            }
            string world = botWorldLine ?? "bots: the match servers' Worlds were not read yet";
            string resets = "bots: input device resets " + botInputResets.ToString(CultureInfo.InvariantCulture) + "; headless-World exceptions this match " +
                            (headlessReports - botHeadlessBase).ToString(CultureInfo.InvariantCulture);
            return new[] { world, ramp.ToString(), resets };
        }

        /// <summary>The median of the last <see cref="BotDtSamples"/> AI-tick dts, in seconds (the lower middle value).</summary>
        private static float BotMedian()
        {
            int n = botCalls < BotDtSamples ? botCalls : BotDtSamples;
            if (n == 0) return 0f;
            var values = new float[n];
            Array.Copy(botDt, values, n);
            return Select(values, (n - 1) / 2);
        }

        /// <summary>The <paramref name="k"/>-th smallest value (quickselect; the game's trimmed mscorlib has no Array.Sort(Array)).</summary>
        internal static float Select(float[] values, int k)
        {
            int left = 0, right = values.Length - 1;
            while (left < right)
            {
                float pivot = values[(left + right) / 2];
                int i = left, j = right;
                while (i <= j)
                {
                    while (values[i] < pivot) i++;
                    while (values[j] > pivot) j--;
                    if (i > j) break;
                    float t = values[i];
                    values[i] = values[j];
                    values[j] = t;
                    i++;
                    j--;
                }
                if (k <= j) right = j;
                else if (k >= i) left = i;
                else break;
            }
            return values[k];
        }

        /// <summary>
        /// One status-line part: the current (or last) match's "bots:" lines, or "bots: no MoveTo ramp call yet" before the first
        /// one (no bot match yet, or no 'botmove' patch).
        /// </summary>
        public static string DescribeBots()
        {
            try
            {
                lock (botGate)
                {
                    if (botMatches == 0)
                    {
                        return "bots: no MoveTo ramp call yet (no bot match, or no 'botmove' patch); settings embedded.bots.moveFromRest " +
                               Bool(botMoveFromRest) + ", input device resets " + botInputResets.ToString(CultureInfo.InvariantCulture);
                    }
                    return "bots: match " + botMatches.ToString(CultureInfo.InvariantCulture) + "\n" + string.Join("\n", BotLines());
                }
            }
            catch (Exception)
            {
                return "bots: unavailable";
            }
        }

        /// <summary>
        /// PC (PcHooks, <c>InputManager.OnDeviceDisconnect</c>): an input device went away, and <c>CharacterComponent.ResetInputsWin</c>
        /// zeroes the movement input of every character, the match servers' bots included: from 0 a bot needs a first step again.
        /// The first 20 lines, then every 50th. Never throws.
        /// </summary>
        internal static void BotInputDeviceReset(string device)
        {
            try
            {
                int n = Interlocked.Increment(ref botInputResets);
                if (n > 20 && n % 50 != 0) return;
                BotInfo("bots: input device disconnected (" + (device ?? "?") + "): the game zeroes every character's movement input, the match " +
                        "servers' bots included (CharacterComponent.ResetInputsWin; reset " + n.ToString(CultureInfo.InvariantCulture) + ")");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Input device resets seen so far (tests, status).</summary>
        public static int BotInputResets { get { return botInputResets; } }

        /// <summary>Resets every bot counter (tests).</summary>
        internal static void ResetBots()
        {
            lock (botGate)
            {
                ClearBotStats();
                botMatches = 0;
                botInputResets = 0;
                botFirstBlockLogged = 0;
                botWorldLine = null;
            }
            botMoveFromRest = true;
            BotClock = () => Environment.TickCount;
        }

        /// <summary>
        /// "bots: server Worlds w2 at 60.1 frames/s (TotalFrames/TotalTime), BT_Root.AiModulo 4 -> 15.0 thinks/s; client FPS cap
        /// 60, Vsync true (in-game)", plus "; pacer limit 144 (settings graphics.pcFpsLimit)" while that setting replaces the in-game
        /// cap, read by reflection from the loaded engine and Game (never a load) on the game thread.
        /// </summary>
        private static string DescribeBotWorlds()
        {
            try
            {
                Type app = Embedded.LoadedTypes.EngineType("Gameloft.Engine.GameApplication");
                PropertyInfo instance = app == null ? null : app.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                object application = instance == null ? null : instance.GetValue(null, null);
                FieldInfo worlds = application == null ? null : app.GetField("Worlds", BindingFlags.Public | BindingFlags.Instance);
                Type root = Embedded.LoadedTypes.Find(Embedded.LoadedTypes.GameAssembly, "Game.BT_Root");
                FieldInfo modulo = root == null ? null : root.GetField("AiModulo", BindingFlags.Public | BindingFlags.Static);
                IDictionary<string, string> prefs = GraphicsHooks.ReadInGamePreferences(
                    Embedded.LoadedTypes.Find(Embedded.LoadedTypes.GameAssembly, "Game.GamePreferencesComponent"));
                return FormatBotWorlds(worlds == null ? null : worlds.GetValue(application) as System.Collections.IEnumerable,
                                       modulo == null ? null : modulo.GetValue(null) as int?, prefs) + GraphicsHooks.FrameRateOverrideNote();
            }
            catch (Exception ex)
            {
                return "bots: the match servers' Worlds could not be read (" + ex.GetType().Name + ")";
            }
        }

        /// <summary>The world line from a <c>GameApplication.Worlds</c> list, <c>BT_Root.AiModulo</c> and the PC preferences (tests).</summary>
        internal static string FormatBotWorlds(System.Collections.IEnumerable worlds, int? aiModulo, IDictionary<string, string> prefs)
        {
            var sb = new System.Text.StringBuilder("bots: ");
            double fps = 0;
            int index = 0, headless = 0;
            if (worlds != null)
            {
                foreach (object w in worlds)
                {
                    index++;
                    if (w == null || !(InstanceValue(w, "Headless") is bool) || !(bool)InstanceValue(w, "Headless")) continue;
                    object frames = InstanceValue(w, "TotalFrames");
                    object time = InstanceValue(w, "TotalTime");
                    object seconds = time == null ? null : InstanceValue(time, "Seconds");
                    double s = seconds == null ? 0 : Convert.ToDouble(seconds, CultureInfo.InvariantCulture);
                    double f = frames == null ? 0 : Convert.ToDouble(frames, CultureInfo.InvariantCulture);
                    sb.Append(headless == 0 ? "server Worlds " : ", ").Append('w').Append(index.ToString(CultureInfo.InvariantCulture));
                    if (s > 0)
                    {
                        fps = f / s;
                        sb.Append(" at ").Append(Number(fps, "0.0"));
                    }
                    else
                    {
                        fps = 0;
                        sb.Append(" at ?");
                    }
                    headless++;
                }
            }
            if (headless == 0) sb.Append("no server World");
            else sb.Append(" frames/s (TotalFrames/TotalTime)");
            if (aiModulo.HasValue)
            {
                sb.Append(", BT_Root.AiModulo ").Append(aiModulo.Value.ToString(CultureInfo.InvariantCulture));
                if (fps > 0 && aiModulo.Value > 0) sb.Append(" -> ").Append(Number(fps / aiModulo.Value, "0.0")).Append(" thinks/s");
            }
            else
            {
                sb.Append(", BT_Root.AiModulo ?");
            }
            string cap, vsync;
            if (prefs != null && prefs.TryGetValue("FPS", out cap))
            {
                sb.Append("; client FPS cap ").Append(cap);
                if (prefs.TryGetValue("Vsync", out vsync)) sb.Append(", Vsync ").Append(vsync);
                sb.Append(" (in-game)");
            }
            return sb.ToString();
        }

        private static object InstanceValue(object target, string name)
        {
            try
            {
                Type t = target.GetType();
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(target, null);
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return f == null ? null : f.GetValue(target);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void BotInfo(string text)
        {
            Log l = log;
            if (l != null) l.Info(text);
            else SelfLog.Write(SelfLog.Info, "[bots] " + text);
        }

        /// <summary>The engine's threading choices (GameOptions), read once by reflection for the boot trace.</summary>
        private static string DescribeEngineThreading()
        {
            try
            {
                Type physics = Embedded.LoadedTypes.EngineType("Gameloft.Engine.PhysicsComponent");
                Type app = Embedded.LoadedTypes.EngineType("Gameloft.Engine.GameApplication");
                string deferred = StaticProperty(physics, "DefaultDeferred");
                object instance = app == null ? null : app.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                string renderThread = InstanceProperty(app, instance, "RenderThreadEnabled");
                string renderMode = InstanceProperty(app, instance, "RenderThreadMode");
                return "engine threading: PhysicsComponent.DefaultDeferred " + deferred + " (client physics on a loading-pool thread), " +
                       "GameApplication.RenderThreadEnabled " + renderThread + ", RenderThreadMode " + renderMode;
            }
            catch (Exception ex)
            {
                return "engine threading unavailable: " + ex.GetType().Name;
            }
        }

        private static string StaticProperty(Type type, string name)
        {
            try
            {
                PropertyInfo p = type == null ? null : type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                object v = p == null ? null : p.GetValue(null, null);
                return v == null ? "?" : Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string InstanceProperty(Type type, object instance, string name)
        {
            try
            {
                if (instance == null) return "? (no GameApplication yet)";
                PropertyInfo p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object v = p == null ? null : p.GetValue(instance, null);
                return v == null ? "?" : Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        private static void Mark(string text)
        {
            BootTrace b = boot;
            if (b != null)
            {
                b.Mark(text);
                return;
            }
            Log l = log;
            if (l != null) l.Info(text);
            else SelfLog.Write(SelfLog.Info, "[boot] " + text);
        }

        private static void Warn(string text)
        {
            BootTrace b = boot;
            if (b != null)
            {
                b.Warn(text);
                return;
            }
            Log l = log;
            if (l != null) l.Warn(text);
            else SelfLog.Write(SelfLog.Warn, "[physics] " + text);
        }
    }
}
